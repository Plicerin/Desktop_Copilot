namespace DesktopCopilot.Skills;

/// <summary>
/// An action skill executes directly and returns the spoken response — no Copilot CLI involved.
/// </summary>
public interface IActionSkill
{
    bool TryMatch(string transcript);
    Task<string> ExecuteAsync(string transcript, CancellationToken cancellationToken);
}
