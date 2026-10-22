"""Stable diagnostic categories; never infer corruption from one QEMU error."""
import errno
import subprocess


def describe(error):
    text=str(error).casefold()
    code='SNAPSHOT_ERROR'
    if isinstance(error,InterruptedError):code='CANCELLED'
    elif isinstance(error,(TimeoutError,subprocess.TimeoutExpired)):code='RESULT_UNCONFIRMED'
    elif getattr(error,'errno',None)==errno.ENOSPC or 'недостаточно места' in text:code='NO_SPACE'
    elif isinstance(error,PermissionError) or getattr(error,'winerror',None)==5:code='ACCESS_DENIED'
    elif isinstance(error,FileNotFoundError) or getattr(error,'winerror',None) in (15,21,55,1167):code='DEVICE_UNAVAILABLE'
    elif any(word in text for word in ('устарел','изменил','изменён','новый план')):code='STALE_PLAN'
    elif any(word in text for word in ('неполная','состав группы')):code='INCOMPLETE_GROUP'
    elif any(word in text for word in ('зарезервировать','занят','другим qemu','другой конфигурац','sharing')):code='IMAGE_BUSY'
    elif any(word in text for word in ('формат','поддержива','backing')):code='UNSUPPORTED_FORMAT'
    return dict(code=code,detail=str(error) or type(error).__name__)
