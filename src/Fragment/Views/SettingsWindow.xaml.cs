using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Fragment.ViewModels;

namespace Fragment.Views;

/// <summary>
/// Code-behind for the settings dialog. Owns wiring the folder picker and the
/// Save/Cancel buttons; all editable state lives on <see cref="SettingsViewModel"/>.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
        _viewModel.BrowseOutputFolder += OnBrowseOutputFolder;
    }

    private void OnBrowseOutputFolder()
    {
        var selected = FolderPicker.PickFolder(this, _viewModel.OutputFolder);
        if (!string.IsNullOrEmpty(selected))
        {
            _viewModel.OutputFolder = selected;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _viewModel.Save();
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.BrowseOutputFolder -= OnBrowseOutputFolder;
        base.OnClosed(e);
    }
}

/// <summary>
/// Minimal Vista-style folder picker using the Win32 <c>IFileOpenDialog</c> COM
/// API. Avoids taking a dependency on WinForms (<c>FolderBrowserDialog</c>).
/// </summary>
internal static class FolderPicker
{
    public static string? PickFolder(Window owner, string? initialPath)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialogRcw();
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);

            if (!string.IsNullOrEmpty(initialPath) && Directory.Exists(initialPath))
            {
                if (SHCreateItemFromParsingName(initialPath, IntPtr.Zero, typeof(IShellItem).GUID, out var item) == 0
                    && item is not null)
                {
                    dialog.SetFolder(item);
                }
            }

            var hwnd = new System.Windows.Interop.WindowInteropHelper(owner).Handle;
            var hr = dialog.Show(hwnd);
            if (hr != 0)
            {
                // User cancelled or an error occurred (S_OK == 0).
                return null;
            }

            dialog.GetResult(out var result);
            result.GetDisplayName(SIGDN_FILESYSPATH, out var path);
            return path;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint FOS_PATHMUSTEXIST = 0x00000800;
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        out IShellItem ppv);

    [ComImport]
    [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogRcw
    {
    }

    [ComImport]
    [Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show([In] IntPtr parent);
        void SetFileTypes();   // not used
        void SetFileTypeIndex([In] uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise();         // not used
        void Unadvise();       // not used
        void SetOptions([In] uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([In, MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([In, MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([In, MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([In, MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int alignment);
        void SetDefaultExtension([In, MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close([MarshalAs(UnmanagedType.Error)] int hr);
        void SetClientGuid([In] ref Guid guid);
        void ClearClientData();
        void SetFilter([MarshalAs(UnmanagedType.Interface)] IntPtr pFilter);
        // IFileOpenDialog-specific members omitted (not needed).
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName([In] uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes([In] uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, [In] uint hint, out int piOrder);
    }
}
