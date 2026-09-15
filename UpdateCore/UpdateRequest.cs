namespace singC.Updates;

public sealed record UpdateRequest(string TargetDirectory, string Version, int ParentId, long ParentStartTicks, bool ResumeProxy);
