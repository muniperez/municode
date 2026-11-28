namespace Municode.Common.Models;

public sealed class WorkspaceMaintenance
{
    public const int SingletonId = 1;

    public int Id { get; set; }
    public bool Enabled { get; set; }
}
