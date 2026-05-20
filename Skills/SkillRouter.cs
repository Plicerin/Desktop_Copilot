namespace DesktopCopilot.Skills;

/// <summary>
/// Routes a voice transcript to a matching skill based on keyword detection.
/// </summary>
public sealed class SkillRouter
{
    private readonly ISkill[] _skills =
    [
        new DiskSpaceSkill(),
        new MemorySkill(),
        new InternetHealthSkill(),
        new WindowsUpdateSkill(),
    ];

    /// <summary>
    /// Returns the first skill whose keywords appear in the transcript, or null if none match.
    /// </summary>
    public ISkill? Match(string transcript)
    {
        var lower = transcript.ToLowerInvariant();
        foreach (var skill in _skills)
        {
            if (skill.Keywords.Any(k => lower.Contains(k)))
                return skill;
        }
        return null;
    }

    public IReadOnlyList<ISkill> AllSkills => _skills;
}
