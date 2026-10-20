"""Windows process observation and per-VM operation locks."""
from contextlib import contextmanager
import ctypes
from ctypes import wintypes
import hashlib
import os
import socket

def kernel_api():
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel.WaitForSingleObject.restype = wintypes.DWORD
    return kernel

@contextmanager
def vm_lock(root, name):
    if os.name != 'nt':
        raise ValueError('Управление VM поддерживается только в Windows.')
    kernel = kernel_api()
    kernel.CreateMutexW.argtypes = [wintypes.LPVOID, wintypes.BOOL, wintypes.LPCWSTR]
    kernel.CreateMutexW.restype = wintypes.HANDLE
    kernel.ReleaseMutex.argtypes = [wintypes.HANDLE]
    key = hashlib.sha256((str(root.resolve()).casefold() + ':' + name).encode()).hexdigest()
    handle = kernel.CreateMutexW(None, False, 'Local\\VMManager-' + key)
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    acquired = False
    try:
        result = kernel.WaitForSingleObject(handle, 0)
        if result not in (0, 0x80):
            raise ValueError('Для этой VM уже открыта панель или выполняется операция. Сначала закрой её: ' + name)
        acquired = True
        yield
    finally:
        if acquired:
            kernel.ReleaseMutex(handle)
        kernel.CloseHandle(handle)

class ProcessWatcher:
    def __init__(self, pid):
        self.kernel = kernel_api()
        self.kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        self.kernel.OpenProcess.restype = wintypes.HANDLE
        self.kernel.GetExitCodeProcess.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
        self.handle = self.kernel.OpenProcess(0x00100000 | 0x1000, False, pid)
        if not self.handle:
            raise ctypes.WinError(ctypes.get_last_error())
        self.pid = pid
        self.returncode = None

    def poll(self):
        result = self.kernel.WaitForSingleObject(self.handle, 0)
        if result == 0x102:
            return None
        if result != 0:
            raise ctypes.WinError(ctypes.get_last_error())
        code = wintypes.DWORD()
        if not self.kernel.GetExitCodeProcess(self.handle, ctypes.byref(code)):
            raise ctypes.WinError(ctypes.get_last_error())
        self.returncode = code.value
        return self.returncode

    def close(self):
        if self.handle:
            self.kernel.CloseHandle(self.handle)
            self.handle = None

def watch_qmp_process(port):
    """Identify the owner of the loopback QMP listener, then keep its process handle."""
    if os.name != 'nt':
        raise ValueError('Наблюдение за QEMU поддерживается только в Windows.')
    api = ctypes.WinDLL('iphlpapi', use_last_error=True)
    api.GetExtendedTcpTable.argtypes = [wintypes.LPVOID, ctypes.POINTER(wintypes.DWORD), wintypes.BOOL, wintypes.ULONG, ctypes.c_int, wintypes.ULONG]
    api.GetExtendedTcpTable.restype = wintypes.DWORD
    size = wintypes.DWORD()
    result = api.GetExtendedTcpTable(None, ctypes.byref(size), False, socket.AF_INET, 3, 0)
    if result not in (0, 122):
        raise OSError(result, 'Не удалось найти процесс QMP.')
    class Row(ctypes.Structure):
        _fields_ = [(name, wintypes.DWORD) for name in ('state', 'local_addr', 'local_port', 'remote_addr', 'remote_port', 'pid')]
    for attempt in range(3):
        buffer = ctypes.create_string_buffer(size.value)
        result = api.GetExtendedTcpTable(buffer, ctypes.byref(size), False, socket.AF_INET, 3, 0)
        if result == 122:
            continue
        if result:
            raise OSError(result, 'Не удалось прочитать процессы QMP.')
        count = wintypes.DWORD.from_buffer_copy(buffer).value
        for index in range(count):
            row = Row.from_buffer_copy(buffer, 4 + index * ctypes.sizeof(Row))
            if socket.ntohs(row.local_port & 0xffff) == port and row.local_addr == int.from_bytes(socket.inet_aton('127.0.0.1'), 'little'):
                return ProcessWatcher(row.pid)
        break
    raise ValueError('QMP не прослушивает ожидаемый порт; VM могла уже завершиться.')
