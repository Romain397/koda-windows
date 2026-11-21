namespace Koda.Core.Tests;

internal sealed class TestSecretStore : ISecretStore
{
    private string? _value;

    public bool FailWrites { get; set; }

    public string? Read() => _value;

    public void Write(string? value)
    {
        if (FailWrites) throw new IOException("Test credential write failed.");
        _value = value;
    }
}
