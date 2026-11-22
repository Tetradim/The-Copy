namespace TetherBridge.Desktop.Services;

public sealed record PairingPayload(string Host, int Port, string Token, string Mode)
{
    public string Fingerprint { get; init; } = "";
    public const string SchemePrefix = "tetherbridge://pair";
}
