using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KittyClaw.Core.Platform;

/// <summary>
/// Windows implementation backed by the native shell folder picker. The dialog is
/// created in-process on its own STA thread, so it stays visible even when KittyClaw
/// itself was launched from a hidden console window.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFolderPicker : IFolderPicker
{
    private const uint FosPickFolders = 0x00000020;
    private const uint FosForceFileSystem = 0x00000040;
    private const uint FosPathMustExist = 0x00000800;
    private const uint FosDontAddToRecent = 0x02000000;
    private const uint SigDnFileSystemPath = 0x80058000;
    private const int ErrorCancelledHResult = unchecked((int)0x800704C7);
    private static readonly Guid FileOpenDialogClassId = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");

    public bool IsAvailable => OperatingSystem.IsWindows();

    public Task<string?> PickFolderAsync(string? initialPath, CancellationToken ct)
    {
        if (!IsAvailable)
            return Task.FromResult<string?>(null);

        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(PickFolder(initialPath));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "KittyClaw folder picker",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ct.CanBeCanceled ? completion.Task.WaitAsync(ct) : completion.Task;
    }

    private static string? PickFolder(string? initialPath)
    {
        IFileOpenDialog? dialog = null;
        IShellItem? initialFolder = null;
        IShellItem? result = null;

        try
        {
            var dialogType = Type.GetTypeFromCLSID(FileOpenDialogClassId, throwOnError: true)!;
            dialog = (IFileOpenDialog)Activator.CreateInstance(dialogType)!;
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FosPickFolders | FosForceFileSystem | FosPathMustExist | FosDontAddToRecent);
            dialog.SetTitle("Choisir le dossier du projet");

            if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
            {
                var shellItemId = typeof(IShellItem).GUID;
                SHCreateItemFromParsingName(Path.GetFullPath(initialPath), IntPtr.Zero, ref shellItemId, out initialFolder);
                dialog.SetFolder(initialFolder);
            }

            var resultCode = dialog.Show(IntPtr.Zero);
            if (resultCode == ErrorCancelledHResult)
                return null;
            Marshal.ThrowExceptionForHR(resultCode);

            dialog.GetResult(out result);
            result.GetDisplayName(SigDnFileSystemPath, out var pathPointer);
            try
            {
                return Marshal.PtrToStringUni(pathPointer);
            }
            finally
            {
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }
        finally
        {
            ReleaseComObject(result);
            ReleaseComObject(initialFolder);
            ReleaseComObject(dialog);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        ref Guid shellItemId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem shellItem);

    [ComImport]
    [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig]
        int Show(IntPtr parent);
        void SetFileTypes(uint fileTypeCount, IntPtr filterSpecs);
        void SetFileTypeIndex(uint fileTypeIndex);
        void GetFileTypeIndex(out uint fileTypeIndex);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem shellItem);
        void SetFolder(IShellItem shellItem);
        void GetFolder(out IShellItem shellItem);
        void GetCurrentSelection(out IShellItem shellItem);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem shellItem);
        void AddPlace(IShellItem shellItem, uint alignment);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int resultCode);
        void SetClientGuid(ref Guid clientGuid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
        void GetResults(out IntPtr shellItems);
        void GetSelectedItems(out IntPtr shellItems);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handlerId, ref Guid interfaceId, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint displayNameType, out IntPtr name);
        void GetAttributes(uint attributeMask, out uint attributes);
        void Compare(IShellItem shellItem, uint hint, out int order);
    }
}
