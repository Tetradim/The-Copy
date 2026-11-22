namespace TetherBridge.Desktop.Services;

public sealed record DiagnosticResult(string Code, bool Ok, string Message, string Probe, string Fix)
{
    public string ToDisplayString() => $"{Code}: {Message} | Probe: {Probe} | Fix: {Fix}";

    public static DiagnosticResult Pass(string code, string message, string probe = "") => new(code, true, message, probe, "No action needed.");
    public static DiagnosticResult Fail(string code, string message, string probe, string fix) => new(code, false, message, probe, fix);
}
