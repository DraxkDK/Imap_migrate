using DKS.Migration.Agent.Models;

namespace DKS.Migration.Agent.Services;

/// <summary>
/// Imports PST files into an Outlook profile via COM automation.
/// Requires Outlook to be installed. Runs in the user session (not SYSTEM).
/// </summary>
public class PstImporter
{
    private readonly ILogger<PstImporter> _logger;

    public PstImporter(ILogger<PstImporter> logger) => _logger = logger;

    public async Task<bool> AttachPstAsync(string pstPath, string profileName, CancellationToken ct)
    {
        return await Task.Run(() => AttachPstViaCom(pstPath, profileName), ct);
    }

    public async Task<bool> ImportPstIntoFolderAsync(string pstPath, string profileName,
        string? targetFolderName, CancellationToken ct)
    {
        return await Task.Run(() => ImportViaCom(pstPath, profileName, targetFolderName), ct);
    }

    private bool AttachPstViaCom(string pstPath, string profileName)
    {
        try
        {
            var outlookType = Type.GetTypeFromProgID("Outlook.Application");
            if (outlookType == null)
            {
                _logger.LogError("Outlook not installed (COM ProgID not found)");
                return false;
            }

            // On Windows, CreateInstance reuses the running Outlook process via COM ROT
            dynamic outlook = Activator.CreateInstance(outlookType)!;
            dynamic ns = outlook.GetNamespace("MAPI");
            ns.AddStore(pstPath);
            _logger.LogInformation("PST attached: {Path}", pstPath);

            System.Runtime.InteropServices.Marshal.ReleaseComObject(ns);
            System.Runtime.InteropServices.Marshal.ReleaseComObject(outlook);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to attach PST via COM: {Path}", pstPath);
            return false;
        }
    }

    private bool ImportViaCom(string pstPath, string profileName, string? targetFolderName)
    {
        try
        {
            var outlookType = Type.GetTypeFromProgID("Outlook.Application");
            if (outlookType == null)
            {
                _logger.LogError("Outlook not installed");
                return false;
            }

            dynamic outlook = Activator.CreateInstance(outlookType)!;
            dynamic ns = outlook.GetNamespace("MAPI");
            ns.AddStore(pstPath);

            // Find the store we just added — release each store object not selected to avoid COM leak
            dynamic stores = ns.Stores;
            dynamic? pstStore = null;
            int storeCount = stores.Count;
            for (int i = 1; i <= storeCount; i++)
            {
                dynamic s = stores[i];
                if (s.FilePath?.ToString()?.Equals(pstPath, StringComparison.OrdinalIgnoreCase) == true)
                    pstStore = s;
                else
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(s);
            }
            System.Runtime.InteropServices.Marshal.ReleaseComObject(stores);

            if (pstStore == null)
            {
                _logger.LogWarning("PST store not found after AddStore");
                System.Runtime.InteropServices.Marshal.ReleaseComObject(ns);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(outlook);
                return false;
            }

            dynamic defaultStore = ns.DefaultStore;
            dynamic targetFolder;

            if (!string.IsNullOrEmpty(targetFolderName))
            {
                dynamic rootFolder = defaultStore.GetRootFolder();
                targetFolder = GetOrCreateFolder(rootFolder, targetFolderName.TrimStart('/'));
                System.Runtime.InteropServices.Marshal.ReleaseComObject(rootFolder);
            }
            else
            {
                targetFolder = ns.GetDefaultFolder(6); // olFolderInbox
            }

            dynamic pstRoot = pstStore.GetRootFolder();
            CopyFolderContents(pstRoot, targetFolder);
            ns.RemoveStore(pstRoot);

            System.Runtime.InteropServices.Marshal.ReleaseComObject(pstRoot);
            System.Runtime.InteropServices.Marshal.ReleaseComObject(targetFolder);
            System.Runtime.InteropServices.Marshal.ReleaseComObject(defaultStore);
            System.Runtime.InteropServices.Marshal.ReleaseComObject(pstStore);
            System.Runtime.InteropServices.Marshal.ReleaseComObject(ns);
            System.Runtime.InteropServices.Marshal.ReleaseComObject(outlook);

            _logger.LogInformation("PST import completed: {Path}", pstPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PST import failed: {Path}", pstPath);
            return false;
        }
    }

    private dynamic GetOrCreateFolder(dynamic parentFolder, string folderName)
    {
        try
        {
            return parentFolder.Folders[folderName];
        }
        catch
        {
            return parentFolder.Folders.Add(folderName);
        }
    }

    private void CopyFolderContents(dynamic sourceFolder, dynamic targetFolder)
    {
        try
        {
            // Copy all items in this folder
            dynamic items = sourceFolder.Items;
            for (int i = 1; i <= items.Count; i++)
            {
                try { items[i].Copy().Move(targetFolder); }
                catch { /* skip individual item failures */ }
            }

            // Recursively handle subfolders
            dynamic subFolders = sourceFolder.Folders;
            for (int i = 1; i <= subFolders.Count; i++)
            {
                dynamic sub = subFolders[i];
                dynamic subTarget = GetOrCreateFolder(targetFolder, sub.Name);
                CopyFolderContents(sub, subTarget);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error copying folder contents from {Folder}", (string)sourceFolder.Name);
        }
    }
}
