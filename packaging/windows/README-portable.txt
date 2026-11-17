QoffeeAutomat - portable distribution
=====================================

This folder holds the same files as the installer puts into "Program Files". Nothing is
registered on your PC until you run one of the commands below.

Try it without installing
-------------------------
    QoffeeAutomat.Agent.exe run

The agent starts in this console. Open http://127.0.0.1:8480/ in a browser. Without the
service it runs in your account and cannot start programs for other users; stop it with
Ctrl+C. Pass --data-dir <folder> to keep all files in a folder of your choice instead of
%ProgramData%\QoffeeAutomat.

Install as a service
--------------------
Copy this folder to a place that only administrators can change (for example
"C:\Program Files\QoffeeAutomat"). The service runs as LocalSystem and starts every file in
that folder, so a folder that standard users can write to would let them run code as
SYSTEM. Then, in an elevated prompt:

    QoffeeAutomat.Agent.exe service install
    QoffeeAutomat.Agent.exe service start

Open the firewall for the QoffeeLink port (8481) on private and domain networks if Home
Assistant runs on another device:

    netsh advfirewall firewall add rule name="QoffeeAutomat QoffeeLink" dir=in action=allow protocol=TCP localport=8481 profile=private,domain

The installer does all of this for you, adds a Start menu shortcut and removes everything
cleanly again. Use it unless you have a reason not to.

First start
-----------
The first start writes a setup token to
    %ProgramData%\QoffeeAutomat\secrets\setup-token.txt
(readable by administrators only). The UI asks for it once, then you set a password.

QoffeeAutomat.SessionHost.exe must stay next to QoffeeAutomat.Agent.exe; the agent starts it
in each user session.
