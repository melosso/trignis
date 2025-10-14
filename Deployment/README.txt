TRIGNIS SERVICE DEPLOYMENT
==========================

TESTING
-------
1. Run 'TrignisBackgroundService.bat test' to test the application in console mode
2. Check logs in the 'log' folder
3. Press Ctrl+C to stop the console test

SERVICE INSTALLATION
--------------------
1. Run 'TrignisBackgroundService.bat install' as Administrator
2. Run 'TrignisBackgroundService.bat start' to start the service
3. Run 'TrignisBackgroundService.bat status' to check if it's running

SERVICE MANAGEMENT
------------------
- Start: TrignisBackgroundService.bat start
- Stop: TrignisBackgroundService.bat stop
- Status: TrignisBackgroundService.bat status
- Uninstall: TrignisBackgroundService.bat uninstall

TROUBLESHOOTING
---------------
If the service fails to start:
1. Check logs in the 'log' folder
2. Run 'TrignisBackgroundService.bat status' to see service status
3. Check Windows Event Viewer > Application logs (if enabled)
4. Test in console mode first using 'TrignisBackgroundService.bat test'

CONFIGURATION
-------------
Edit configuration files in the 'Environments' folder
Default environment: Production
