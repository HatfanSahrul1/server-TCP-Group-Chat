@echo off
title WhatsApp95 Chat Server
echo =======================================
echo     WhatsApp95 Chat Server
echo =======================================
echo.
echo Starting server on port 8888...
echo Commands: /list, /stop, /help
echo.
cd /d "c:\Users\THINKPAD\dotnet_kuliah\serverChatTCP"
dotnet run
echo.
echo Server has stopped.
pause