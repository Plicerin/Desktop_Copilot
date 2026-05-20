namespace DesktopCopilot.Skills;

public interface ISkill
{
    string Name { get; }
    string[] Keywords { get; }
    Task<string> RunAsync(CancellationToken cancellationToken);
}
