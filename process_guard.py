"""Do not leave an orphan GPU worker running after its owning API exits."""
import os
import threading
import time

def watch_parent():
    pid = int(os.getenv("SAIL_PARENT_PID", "0"))
    if not pid: return
    def watch():
        if os.name == "nt":
            import ctypes
            from ctypes import wintypes
            kernel = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
            kernel.OpenProcess.restype = wintypes.HANDLE
            kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
            kernel.CloseHandle.argtypes = [wintypes.HANDLE]
            handle = kernel.OpenProcess(0x00100000, False, pid)
            if not handle: os._exit(2)
            try:
                if kernel.WaitForSingleObject(handle, 0xFFFFFFFF) == 0: os._exit(2)
            finally: kernel.CloseHandle(handle)
        else:
            while True:
                if os.getppid() != pid: os._exit(2)
                time.sleep(2)
    threading.Thread(target=watch, daemon=True).start()
