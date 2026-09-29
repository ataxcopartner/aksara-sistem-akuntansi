namespace Aksara.Desktop.Controls;

public interface IWorkspaceCloseGuard
{
    Task<bool> ConfirmCloseAsync();
}
