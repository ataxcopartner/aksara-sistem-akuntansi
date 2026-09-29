using Aksara.Contracts;

namespace Aksara.Desktop.Services;

public static class InitialSetupFlowPolicy
{
    public static bool RequiresSetup(InitialSetupDto setup) =>
        !setup.LegacyProtected && !setup.Status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase);

    public static bool CanCloseSetup(InitialSetupDto? setup) =>
        setup is not null && !RequiresSetup(setup);
}
