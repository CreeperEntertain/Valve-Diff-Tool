namespace Valve_Diff_Tool.Models
{
    public sealed record BuildArchive(
        string Name,
        string FullPath,
        int DepotId,
        int DepotVersion,
        DateOnly Date,
        TimeOnly Time
    );
}
