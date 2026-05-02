// DevMindProbe — standalone verification probe for tool-call loop.
// Stage 2D: interactive + 5-scenario mode with behavioral observation tracking.
// Run against a local llama-server at http://localhost:8080.
#nullable enable

using DevMind;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DevMindProbe
{
    class Program
    {
        static readonly (string Name, string Task)[] Scenarios = new[]
        {
            ("multi_file_read",
                "Read StringHelper.cs and CsvParser.cs from C:\\Users\\pkailas\\source\\repos\\DevMindTestBed and tell me which method names appear in both files."),

            ("read_plus_grep",
                "Find every method in C:\\Users\\pkailas\\source\\repos\\DevMindTestBed that takes a string parameter. List the file and method name for each."),

            ("counted_output",
                "How many .cs files are in C:\\Users\\pkailas\\source\\repos\\DevMindTestBed? List them all."),

            ("specific_lookup",
                "Look at the WordCount method in StringHelper.cs in C:\\Users\\pkailas\\source\\repos\\DevMindTestBed. What value does it return for an empty string?"),

            ("failed_path_recovery",
                "Read CnfigFileParser.cs from C:\\Users\\pkailas\\source\\repos\\DevMindTestBed and summarize what it does."),
        };

        const int MaxIterations = 30;
        static readonly TimeSpan MaxWallClock = TimeSpan.FromMinutes(5);

        static async Task Main(string[] args)
        {
            Console.WriteLine("=== DevMindProbe — Stage 2D: Scenario Runner ===");
            Console.WriteLine();

            DevMindOptions.UseInMemoryDefaults();
            Console.WriteLine($"[PROBE] Endpoint: {DevMindOptions.Instance.EndpointUrl}");
            Console.WriteLine($"[PROBE] DirectiveMode: {DevMindOptions.Instance.DirectiveMode}");
            Console.WriteLine();

            var client = new LlmClient();
            client.Configure(DevMindOptions.Instance.EndpointUrl, DevMindOptions.Instance.ApiKey);

            Console.WriteLine("[PROBE] Waiting for context detection...");
            await Task.Delay(3000);
            Console.WriteLine("[PROBE] Ready.");
            Console.WriteLine();

            if (args.Length >= 2 && args[0] == "--scenario")
            {
                if (!int.TryParse(args[1], out int idx) || idx < 1 || idx > Scenarios.Length)
                {
                    Console.Error.WriteLine($"ERROR: --scenario must be 1..{Scenarios.Length}");
                    Environment.Exit(1);
                }
                var (name, task) = Scenarios[idx - 1];
                await RunScenario(client, name, task);
            }
            else if (args.Length == 0)
            {
                await RunInteractive(client);
            }
            else
            {
                Console.Error.WriteLine("Usage: dotnet run            (interactive)");
                Console.Error.WriteLine("       dotnet run -- --scenario N  (run scenario 1..5)");
                Environment.Exit(1);
            }
        }

        // ── Interactive mode ──────────────────────────────────────────────────

        static async Task RunInteractive(LlmClient client)
        {
            while (true)
            {
                Console.Write("Enter task (or 'quit' to exit):\n> ");
                string? input = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(input) || input.Trim().Equals("quit", StringComparison.OrdinalIgnoreCase))
                    break;

                client.ClearHistory();
                await RunScenario(client, "interactive", input.Trim());
                Console.WriteLine();
            }
        }

        // ── Scenario runner ───────────────────────────────────────────────────

        static async Task RunScenario(LlmClient client, string scenarioName, string task)
        {
            Console.WriteLine($"┌─ SCENARIO: {scenarioName}");
            Console.WriteLine($"│  TASK: {Truncate(task, 120)}");
            Console.WriteLine("└" + new string('─', 60));
            Console.WriteLine();

            client.ClearHistory();

            bool taskDone = false;
            string? finalAnswer = null;
            int iteration = 0;
            bool converged = false;
            string? abortReason = null;

            var unexpectedBehaviors = new HashSet<string>();
            var seenToolCalls = new HashSet<string>();

            var wallClock = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(MaxWallClock);

            string currentMessage = task;

            while (!taskDone && iteration < MaxIterations)
            {
                if (cts.IsCancellationRequested)
                {
                    abortReason = "timeout";
                    break;
                }

                iteration++;
                Console.WriteLine($"  ── ITERATION {iteration} ──");

                Exception? caughtError = null;
                client.IncrementTurn();

                try
                {
                    await client.SendMessageAsync(
                        currentMessage,
                        onToken: token => Console.Write(token),
                        onComplete: () => { },
                        onError: ex => { caughtError = ex; },
                        cancellationToken: cts.Token
                    );
                }
                catch (OperationCanceledException)
                {
                    abortReason = "timeout";
                    break;
                }

                Console.WriteLine();

                if (caughtError != null)
                {
                    Console.WriteLine($"  [ERROR] {caughtError.GetType().Name}: {Truncate(caughtError.Message, 200)}");
                    abortReason = "server_error";
                    break;
                }

                var toolCalls = client.LastToolCalls;

                if (toolCalls == null || toolCalls.Count == 0)
                {
                    Console.WriteLine("  [PROBE] Model returned prose, no tool calls.");
                    unexpectedBehaviors.Add("prose_finish");
                    taskDone = true;
                    finalAnswer = "(prose — see transcript)";
                    converged = true;
                    break;
                }

                Console.WriteLine($"\n  [PROBE] Tool calls: {toolCalls.Count}");
                foreach (var tc in toolCalls)
                {
                    string argSummary = tc.Arguments != null
                        ? string.Join(", ", tc.Arguments.Select(kv => $"{kv.Key}={Truncate(kv.Value, 60)}"))
                        : "(none)";

                    string callKey = $"{tc.Name}|{string.Join("|", (tc.Arguments ?? new Dictionary<string, string>()).Select(kv => kv.Value))}";
                    if (!seenToolCalls.Add(callKey))
                        unexpectedBehaviors.Add("loop_repeated");

                    Console.WriteLine($"  [CALL] {tc.Name}({Truncate(argSummary, 100)})");

                    if (IsRelativePath(tc))
                        unexpectedBehaviors.Add("relative_paths");

                    if (IsWrongTool(tc, toolCalls))
                        unexpectedBehaviors.Add("wrong_tool");

                    string result = ExecuteTool(tc, ref taskDone, ref finalAnswer);

                    if (result.StartsWith("ERROR:"))
                        unexpectedBehaviors.Add("tool_error");

                    Console.WriteLine($"  [RESULT] {tc.Name} => {Truncate(result, 200)}");
                    client.AddToolResultMessage(tc.Id, result);
                }

                if (taskDone)
                {
                    converged = true;
                    break;
                }

                currentMessage = "Continue with the task.";
                Console.WriteLine();
            }

            if (!converged && abortReason == null)
            {
                if (iteration >= MaxIterations)
                    abortReason = "iteration_limit";
                converged = false;
            }
            else if (taskDone)
            {
                converged = true;
            }

            Console.WriteLine();
            Console.WriteLine(new string('─', 60));

            string outcomeLabel;
            if (taskDone && finalAnswer != null && finalAnswer != "(prose — see transcript)")
                outcomeLabel = $"task_done with answer: {Truncate(finalAnswer, 80)}";
            else if (finalAnswer == "(prose — see transcript)")
                outcomeLabel = "model returned prose, no task_done";
            else if (abortReason == "iteration_limit")
                outcomeLabel = "iteration limit hit";
            else if (abortReason == "timeout")
                outcomeLabel = "wall-clock timeout hit";
            else if (abortReason == "server_error")
                outcomeLabel = "server error";
            else
                outcomeLabel = "loop ended without task_done";

            Console.WriteLine($"OUTCOME: {outcomeLabel}");
            Console.WriteLine($"RESULT: scenario={scenarioName} converged={converged} iterations={iteration} final_answer={Truncate(finalAnswer ?? "(none)", 60)} unexpected_behavior={string.Join(",", unexpectedBehaviors.Count > 0 ? unexpectedBehaviors : new HashSet<string> { "none" })}");
            Console.WriteLine(new string('─', 60));
            Console.WriteLine();
        }

        // ── Tool execution ────────────────────────────────────────────────────

        static string ExecuteTool(ToolCallResult tc, ref bool taskDone, ref string? finalAnswer)
        {
            try
            {
                switch (tc.Name)
                {
                    case "read_file":
                    {
                        tc.Arguments!.TryGetValue("filename", out string? path);
                        if (string.IsNullOrEmpty(path))
                            return "ERROR: 'filename' argument missing";
                        if (!File.Exists(path))
                            return $"ERROR: File not found: {path}";
                        return File.ReadAllText(path);
                    }

                    case "glob":
                    {
                        tc.Arguments!.TryGetValue("pattern", out string? pattern);
                        tc.Arguments!.TryGetValue("directory", out string? dir);
                        if (string.IsNullOrEmpty(pattern))
                            return "ERROR: 'pattern' argument missing";
                        string searchDir = string.IsNullOrEmpty(dir)
                            ? @"C:\Users\pkailas\source\repos\DevMindTestBed"
                            : dir;
                        if (!Directory.Exists(searchDir))
                            return $"ERROR: Directory not found: {searchDir}";
                        string filePattern = Path.GetFileName(pattern) ?? "*.cs";
                        var files = Directory.GetFiles(searchDir, filePattern, SearchOption.TopDirectoryOnly);
                        return files.Length > 0
                            ? string.Join("\n", files.Select(f => Path.GetFileName(f)))
                            : $"No files matching '{pattern}' in {searchDir}";
                    }

                    case "grep_file":
                    {
                        string? path = null;
                        string? pattern = null;
                        tc.Arguments!.TryGetValue("filename", out path);
                        tc.Arguments!.TryGetValue("pattern", out pattern);
                        if (string.IsNullOrEmpty(path) || !File.Exists(path))
                            return $"ERROR: file not found: {path}";
                        var lines = File.ReadAllLines(path);
                        var matches = new System.Text.StringBuilder();
                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (lines[i].IndexOf(pattern ?? "", StringComparison.OrdinalIgnoreCase) >= 0)
                                matches.AppendLine($"{i + 1}: {lines[i]}");
                        }
                        return matches.Length > 0 ? matches.ToString() : $"No matches for '{pattern}'";
                    }

                    case "find_in_files":
                    {
                        tc.Arguments!.TryGetValue("glob", out string? glob);
                        tc.Arguments!.TryGetValue("pattern", out string? pattern);
                        tc.Arguments!.TryGetValue("directory", out string? dir);
                        string searchDir = string.IsNullOrEmpty(dir)
                            ? @"C:\Users\pkailas\source\repos\DevMindTestBed"
                            : dir;
                        string filePattern = string.IsNullOrEmpty(glob) ? "*.cs" : Path.GetFileName(glob) ?? "*.cs";
                        if (!Directory.Exists(searchDir))
                            return $"ERROR: Directory not found: {searchDir}";
                        var files = Directory.GetFiles(searchDir, filePattern, SearchOption.TopDirectoryOnly);
                        var sb = new System.Text.StringBuilder();
                        int count = 0;
                        foreach (var f in files)
                        {
                            var lines = File.ReadAllLines(f);
                            for (int i = 0; i < lines.Length && count < 200; i++)
                            {
                                if (lines[i].IndexOf(pattern ?? "", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    sb.AppendLine($"{Path.GetFileName(f)}:{i + 1}: {lines[i]}");
                                    count++;
                                }
                            }
                        }
                        return sb.Length > 0 ? sb.ToString() : $"No matches for '{pattern}' in {filePattern} under {searchDir}";
                    }

                    case "list_files":
                    {
                        tc.Arguments!.TryGetValue("glob", out string? glob);
                        tc.Arguments!.TryGetValue("recursive", out string? recursiveStr);
                        bool recursive = recursiveStr == null || bool.TryParse(recursiveStr, out bool r) && r;
                        var dir = @"C:\Users\pkailas\source\repos\DevMindTestBed";
                        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                        var excludedSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            { "bin", "obj", ".vs", ".git", "node_modules", "packages" };
                        // Split glob into dir prefix + file pattern (e.g. "Services/*.cs")
                        string normalizedGlob = (glob ?? "").Replace('\\', '/');
                        string filePattern = normalizedGlob;
                        string effectiveRoot = dir;
                        int lastSlash = normalizedGlob.LastIndexOf('/');
                        if (lastSlash >= 0)
                        {
                            string dirPart = normalizedGlob.Substring(0, lastSlash);
                            filePattern = normalizedGlob.Substring(lastSlash + 1);
                            string candidate = Path.Combine(dir, dirPart.Replace('/', Path.DirectorySeparatorChar));
                            if (Directory.Exists(candidate))
                                effectiveRoot = candidate;
                        }
                        try
                        {
                            var matches = Directory.EnumerateFiles(effectiveRoot, filePattern, searchOption)
                                .Where(p => !p.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
                                              .Any(seg => excludedSegments.Contains(seg)))
                                .Select(Path.GetFullPath)
                                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                                .Take(200)
                                .ToList();
                            return matches.Count == 0 ? "[no matches]" : string.Join(Environment.NewLine, matches);
                        }
                        catch (Exception ex)
                        {
                            return $"[ERROR: {ex.Message}]";
                        }
                    }

                    case "run_shell":
                        return "ERROR: run_shell disabled in probe";

                    case "task_done":
                    {
                        string? summary = null;
                        tc.Arguments?.TryGetValue("summary", out summary);
                        finalAnswer = summary ?? "(no summary argument)";
                        taskDone = true;
                        return "[Task marked complete]";
                    }

                    case "scratchpad":
                        return "[Scratchpad updated]";

                    default:
                        return $"ERROR: Tool '{tc.Name}' not implemented in probe";
                }
            }
            catch (Exception ex)
            {
                return $"ERROR: {ex.GetType().Name}: {ex.Message}";
            }
        }

        // ── Behavior detectors ────────────────────────────────────────────────

        static bool IsRelativePath(ToolCallResult tc)
        {
            if (tc.Name != "read_file" && tc.Name != "grep_file") return false;
            string? path = null;
            tc.Arguments?.TryGetValue("filename", out path);
            if (string.IsNullOrEmpty(path)) return false;
            return !Path.IsPathRooted(path);
        }

        static bool IsWrongTool(ToolCallResult tc, IList<ToolCallResult> allCalls)
        {
            // Flag if model used run_shell when it could have used read_file/glob/find_in_files
            return tc.Name == "run_shell";
        }

        // ── Utility ───────────────────────────────────────────────────────────

        static string Truncate(string? s, int max) =>
            s == null ? "(null)" : s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
