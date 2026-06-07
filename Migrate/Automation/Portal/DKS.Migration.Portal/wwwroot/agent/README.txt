Drop built agent binaries in this folder when you want the portal to serve them.

- DKSProfileAgent.msi
  Used by the "Download MSI" button.

- DKSProfileAgent.exe
  Used by the "Download EXE Package (.zip)" button. The portal will wrap this
  EXE together with a generated appsettings.json and README.txt for the
  selected batch/token.

If you keep the standalone EXE somewhere else, set:

  AgentPackage:StandaloneExePath

in appsettings, user secrets, or the environment variable:

  AgentPackage__StandaloneExePath
