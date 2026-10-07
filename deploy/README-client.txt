Pairnets for Windows
==================

1. Put Pairnets.exe in a permanent place, for example
   %LocalAppData%\Programs\Pairnets\Pairnets.exe
2. Run it. Enter:
   - Server URL: https://sync.example.com/  (your tunnel's address, printed by install.sh on the server)
   - Token:      printed by install.sh on the server
   - Folder:     the folder to keep in sync, e.g. D:\Work
   - Device name: unique per PC (defaults to the computer name)
   Press "Test connection", then "Start syncing".
3. Optional: right-click the tray icon and tick "Start with Windows".

Pairnets runs in the notification area. Logs: %LocalAppData%\Pairnets\logs
Settings: %AppData%\Pairnets\settings.json (the token is encrypted with Windows DPAPI).

Full documentation: https://github.com/MRnigth/Pairnets#readme
