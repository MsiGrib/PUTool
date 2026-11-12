namespace PUTool.Infrastructure.Models;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
    public string CombinedOutput => string.Join(Environment.NewLine,
        new[] { StandardOutput.Trim(), StandardError.Trim() }.Where(x => x.Length > 0));
}