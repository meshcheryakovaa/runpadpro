Set fso = CreateObject("Scripting.FileSystemObject")
Set sh = CreateObject("WScript.Shell")
root = fso.GetParentFolderName(WScript.ScriptFullName)
sh.Run Chr(34) & root & "\app\shell\Runpad.Shell.exe" & Chr(34), 1, False
