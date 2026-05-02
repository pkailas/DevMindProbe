// DevMindOptions.Probe.cs — standalone Instance management for probe use.
// Provides the partial class stub that DevMindOptions.Data.cs expects when PROBE is defined.
// NOT compiled into the DevMind VSIX; only used by DevMindProbe.
#nullable enable
#pragma warning disable CS0067

// No usings needed — everything here is in the DevMind namespace and uses only BCL types.

namespace DevMind
{
    // The Instance property and UseInMemoryDefaults() method live in DevMindOptions.Data.cs
    // under #if PROBE guards. This file is the "other half" of that partial class —
    // it exists so the partial compiles cleanly without BaseOptionModel<T> from VS Toolkit.
    public partial class DevMindOptions
    {
        // Stub for VS-side event referenced nowhere in probe code, but present so any
        // compiled reference (e.g., in LlmClient indirectly) doesn't break.
        public static event System.Action? ProfileChanged;
    }
}
