using Microsoft.Win32;

namespace Futureburn.Gui;

// Every Windows common file dialog in this app runs on its own fresh STA
// thread — never the WPF UI thread.
//
// Why: on this host the SECOND common dialog shown on the WPF UI thread hangs
// hard inside the native dialog — CommonItemDialog.RunDialog enters native code
// and never returns, and Windows reports AppHangB1. The first dialog works;
// every one after it deadlocks. A captured stack of the frozen UI thread
// confirmed the freeze is inside the native common-item-dialog modal loop.
//
// Giving each dialog a brand-new STA thread sidesteps it completely: every
// dialog is "the first" on a clean thread. The UI thread just awaits the
// returned Task and keeps pumping messages, so the app stays responsive.
//
// Trade-off: the dialog isn't owner-parented to a window — purely cosmetic.

internal static class FileDialogs
{
    /// <summary>Open-file picker. Returns the chosen path, or null if cancelled.</summary>
    internal static Task<string?> OpenFileAsync(string title, string filter)
        => RunAsync(() =>
        {
            var d = new OpenFileDialog { Title = title, Filter = filter };
            return d.ShowDialog() == true ? d.FileName : null;
        });

    /// <summary>Multi-select open-file picker. Returns the paths, or null if cancelled.</summary>
    internal static Task<string[]?> OpenFilesAsync(string title, string filter)
        => RunAsync(() =>
        {
            var d = new OpenFileDialog { Title = title, Filter = filter, Multiselect = true };
            return d.ShowDialog() == true ? d.FileNames : null;
        });

    /// <summary>Folder picker. Returns the chosen folder, or null if cancelled.</summary>
    internal static Task<string?> OpenFolderAsync(string title)
        => RunAsync(() =>
        {
            var d = new OpenFolderDialog { Title = title };
            return d.ShowDialog() == true ? d.FolderName : null;
        });

    /// <summary>Save-file picker. Returns the chosen path, or null if cancelled.</summary>
    internal static Task<string?> SaveFileAsync(
        string title, string filter, string defaultFileName, string defaultExt)
        => RunAsync(() =>
        {
            var d = new SaveFileDialog
            {
                Title      = title,
                Filter     = filter,
                FileName   = defaultFileName,
                DefaultExt = defaultExt,
            };
            return d.ShowDialog() == true ? d.FileName : null;
        });

    // Run a dialog on a dedicated STA thread; complete the Task with its result.
    private static Task<T> RunAsync<T>(Func<T> showDialog)
    {
        var tcs = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(showDialog()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
