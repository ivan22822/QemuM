"""Windows image leases: deny other writers/deletion, copy via owned handle.

qemu-img must operate only on a private clone. It cannot open the leased source.
The lease survives handle-based renames, allowing later multi-image publication
without a window in which a published image is writable by another process.
"""
from contextlib import ExitStack, contextmanager
import ctypes
from ctypes import wintypes
import os
from pathlib import Path
from vm_storage import safe_file


class ImageLease:
    def __init__(self, path):
        self.path = Path(path)
        self.handle = None
        if os.name != 'nt':
            raise ValueError('Непрерывное владение образами реализовано только для Windows.')
        safe_file(self.path)
        before = self.path.stat()
        self.api = ctypes.WinDLL('kernel32', use_last_error=True)
        self.api.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
        self.api.CreateFileW.restype = wintypes.HANDLE
        self.api.CloseHandle.argtypes = [wintypes.HANDLE]
        self.api.ReadFile.argtypes = [wintypes.HANDLE, wintypes.LPVOID, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD), wintypes.LPVOID]
        self.api.SetFilePointerEx.argtypes = [wintypes.HANDLE, ctypes.c_longlong, ctypes.POINTER(ctypes.c_longlong), wintypes.DWORD]
        self.api.SetFileInformationByHandle.argtypes = [wintypes.HANDLE, ctypes.c_int, wintypes.LPVOID, wintypes.DWORD]
        self.api.GetFinalPathNameByHandleW.argtypes = [wintypes.HANDLE, wintypes.LPWSTR, wintypes.DWORD, wintypes.DWORD]
        self.handle = self.api.CreateFileW(str(self.path), 0x80000000 | 0x10000, 1, None, 3, 0x08000000, None)
        if self.handle == ctypes.c_void_p(-1).value:
            self.handle = None
            raise OSError(ctypes.get_last_error(), 'Не удалось зарезервировать образ. Он открыт другой программой: ' + str(self.path))
        try:
            after = self.path.stat()
            if (before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns) != (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns):
                raise ValueError('Файл образа изменился при получении владения.')
            self.file_id = [after.st_dev, after.st_ino]
            self.size = after.st_size
        except BaseException:
            self.close()
            raise

    def close(self):
        if self.handle is not None:
            self.api.CloseHandle(self.handle)
            self.handle = None

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.close()

    def copy_to(self, target, progress=lambda done, total:None, cancelled=lambda:False):
        if self.handle is None:
            raise ValueError('Владение образом уже завершено.')
        target = Path(target)
        safe_file(target)
        if not self.api.SetFilePointerEx(self.handle, 0, None, 0):
            raise ctypes.WinError(ctypes.get_last_error())
        created = False
        try:
            with target.open('xb') as stream:
                created = True
                buffer = ctypes.create_string_buffer(4 * 1024 * 1024)
                done = 0
                while True:
                    if cancelled():
                        raise InterruptedError('Подготовка копии отменена до публикации.')
                    amount = wintypes.DWORD()
                    if not self.api.ReadFile(self.handle, buffer, len(buffer), ctypes.byref(amount), None):
                        raise ctypes.WinError(ctypes.get_last_error())
                    if not amount.value:
                        break
                    stream.write(buffer.raw[:amount.value])
                    done += amount.value
                    progress(done, self.size)
                if done != self.size:
                    raise ValueError('Размер резервируемого образа изменился.')
                stream.flush()
                os.fsync(stream.fileno())
        except BaseException:
            if created:
                target.unlink()
            raise

    def rename_to(self, target):
        """Rename our exact open file; never replace an existing target."""
        if self.handle is None:
            raise ValueError('Владение образом уже завершено.')
        target = Path(target).absolute()
        safe_file(target)
        if target.exists():
            raise ValueError('Целевой путь уже существует; замена запрещена.')
        class RenameInfo(ctypes.Structure):
            _fields_ = [('ReplaceIfExists', wintypes.BOOL), ('RootDirectory', wintypes.HANDLE), ('FileNameLength', wintypes.DWORD), ('FileName', wintypes.WCHAR * 1)]
        encoded = str(target).encode('utf-16-le')
        offset = RenameInfo.FileName.offset
        buffer = ctypes.create_string_buffer(max(ctypes.sizeof(RenameInfo), offset + len(encoded) + 2))
        info = ctypes.cast(buffer, ctypes.POINTER(RenameInfo)).contents
        info.ReplaceIfExists = False
        info.FileNameLength = len(encoded)
        ctypes.memmove(ctypes.addressof(buffer) + offset, encoded, len(encoded))
        if not self.api.SetFileInformationByHandle(self.handle, 3, buffer, len(buffer)):
            raise ctypes.WinError(ctypes.get_last_error())
        actual = ctypes.create_unicode_buffer(32768)
        if not self.api.GetFinalPathNameByHandleW(self.handle, actual, len(actual), 0):
            raise ctypes.WinError(ctypes.get_last_error())
        self.path = Path(actual.value[4:] if actual.value.startswith('\\\\?\\') else actual.value)
        if os.path.normcase(str(self.path)) != os.path.normcase(str(target)):
            raise ValueError('Путь после переименования отличается от ожидаемого: ' + str(self.path))
        if [target.stat().st_dev, target.stat().st_ino] != self.file_id:
            raise ValueError('Личность образа изменилась после переименования.')


@contextmanager
def lease_images(paths):
    with ExitStack() as stack:
        leases = [stack.enter_context(ImageLease(path)) for path in sorted(paths, key=lambda path:str(path).casefold())]
        if len({tuple(lease.file_id) for lease in leases}) != len(leases):
            raise ValueError('Один физический образ указан несколько раз.')
        yield leases
