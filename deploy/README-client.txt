Tether for Windows
==================

1. Put Tether.exe in a permanent place, for example
   %LocalAppData%\Programs\Tether\Tether.exe
2. Run it. Enter:
   - Server URL: http://<server-tailscale-ip>:5075/  (printed by install.sh on the server)
   - Token:      printed by install.sh on the server
   - Folder:     the folder to keep in sync, e.g. D:\Work
   - Device name: unique per PC (defaults to the computer name)
   Press "Test connection", then "Start syncing".
3. Optional: right-click the tray icon and tick "Start with Windows".

Tether runs in the notification area. Logs: %LocalAppData%\Tether\logs
Settings: %AppData%\Tether\settings.json (the token is encrypted with Windows DPAPI).

Full documentation: https://github.com/MRnigth/Tether#readme
