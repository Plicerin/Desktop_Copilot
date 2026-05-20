namespace DesktopCopilot.Skills;

/// <summary>
/// Routes voice transcripts to action skills that execute directly (no Copilot CLI).
/// Action skills are checked before PC health skills and general Copilot requests.
/// </summary>
public sealed class ActionSkillRouter
{
    private readonly IActionSkill[] _skills;

    public ActionSkillRouter(Func<string, Task> speak)
    {
        _skills =
        [
            new LockComputerSkill(),
            new ScreenshotSkill(),
            new ClipboardSkill(),
            new ReminderSkill(speak),
            new OpenAppSkill(),   // last — broad "open/launch/start" catch-all
        ];
    }

    public IActionSkill? Match(string transcript)
    {
        foreach (var skill in _skills)
        {
            if (skill.TryMatch(transcript))
                return skill;
        }
        return null;
    }
}
