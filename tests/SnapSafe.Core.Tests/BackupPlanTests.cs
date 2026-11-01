using SnapSafe.Core;
using Xunit;

namespace SnapSafe.Core.Tests;

public class BackupPlanTests
{
    [Fact]
    public void BackupPlanKeepsSourceAndDestination()
    {
        var plan = new BackupPlan(@"C:\Source", @"D:\Backups");
        Assert.Equal(@"C:\Source", plan.SourceDirectory);
        Assert.Equal(@"D:\Backups", plan.DestinationDirectory);
    }
}
