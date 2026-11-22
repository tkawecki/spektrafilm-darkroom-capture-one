"""Read-only Windows display/ICC inventory. No window, device or registry writes.

Uses documented Win32/WCS queries; run in the same interactive user/session as C1.
QueryDisplayConfig structures follow Windows SDK 10.0.26100 wingdi.h.
"""
import ctypes as C
from ctypes import wintypes as W
import hashlib
import json
import os
from pathlib import Path
import sys
import winreg

U32 = C.c_uint32
class Device(C.Structure):
    _fields_ = [('cb', U32), ('name', W.WCHAR * 32), ('description', W.WCHAR * 128),
                ('flags', U32), ('device_id', W.WCHAR * 128), ('key', W.WCHAR * 128)]
class Luid(C.Structure):
    _fields_ = [('low', U32), ('high', C.c_int32)]
class Source(C.Structure):
    _fields_ = [('adapter', Luid), ('id', U32), ('mode', U32), ('flags', U32)]
class Rational(C.Structure):
    _fields_ = [('num', U32), ('den', U32)]
class Target(C.Structure):
    _fields_ = [('adapter', Luid), ('id', U32), ('mode', U32), ('technology', U32),
                ('rotation', U32), ('scaling', U32), ('refresh', Rational),
                ('scanline', U32), ('available', W.BOOL), ('flags', U32)]
class DisplayPath(C.Structure):
    _fields_ = [('source', Source), ('target', Target), ('flags', U32)]
class Header(C.Structure):
    _fields_ = [('type', U32), ('size', U32), ('adapter', Luid), ('id', U32)]
class Advanced(C.Structure):
    _fields_ = [('header', Header), ('flags', U32), ('encoding', U32), ('bits', U32)]
class Advanced2(C.Structure):
    _fields_ = Advanced._fields_ + [('mode', U32)]
class SourceName(C.Structure):
    _fields_ = [('header', Header), ('name', W.WCHAR * 32)]
class TargetName(C.Structure):
    _fields_ = [('header', Header), ('flags', U32), ('technology', U32),
                ('manufacturer', W.WORD), ('product', W.WORD), ('connector', U32),
                ('name', W.WCHAR * 64), ('path', W.WCHAR * 128)]

user = C.WinDLL('user32', use_last_error=True)
gdi = C.WinDLL('gdi32', use_last_error=True)
wcs = C.WinDLL('mscms', use_last_error=True)
user.EnumDisplayDevicesW.argtypes = [W.LPCWSTR, U32, C.POINTER(Device), U32]
gdi.CreateDCW.argtypes = [W.LPCWSTR, W.LPCWSTR, W.LPCWSTR, C.c_void_p]
gdi.CreateDCW.restype = C.c_void_p
gdi.DeleteDC.argtypes = [C.c_void_p]
gdi.GetICMProfileW.argtypes = [C.c_void_p, C.POINTER(U32), W.LPWSTR]
wcs.WcsGetUsePerUserProfiles.argtypes = [W.LPCWSTR, U32, C.POINTER(W.BOOL)]
wcs.WcsGetDefaultColorProfileSize.argtypes = [U32, W.LPCWSTR, U32, U32, U32, C.POINTER(U32)]
wcs.WcsGetDefaultColorProfile.argtypes = [U32, W.LPCWSTR, U32, U32, U32, U32, W.LPWSTR]
wcs.ColorProfileGetDisplayUserScope.argtypes = [Luid, U32, C.POINTER(U32)]
wcs.ColorProfileGetDisplayDefault.argtypes = [U32, Luid, U32, U32, U32, C.POINTER(C.c_void_p)]
kernel = C.WinDLL('kernel32', use_last_error=True)
kernel.LocalFree.argtypes = [C.c_void_p]
kernel.LocalFree.restype = C.c_void_p

def profile_metadata(path):
    p = Path(path)
    if not p.is_absolute():
        p = Path(os.environ.get('WINDIR', 'C:/Windows')) / 'System32/spool/drivers/color' / p
    if not p.is_file():
        return {'path': str(p), 'exists': False}
    b = p.read_bytes()
    return {'path': str(p), 'bytes': len(b), 'sha256': hashlib.sha256(b).hexdigest(),
            'device_class': b[12:16].decode('ascii', 'replace'),
            'color_space': b[16:20].decode('ascii', 'replace'),
            'pcs': b[20:24].decode('ascii', 'replace'),
            'signature': b[36:40].decode('ascii', 'replace')}

def icm(device):
    dc = gdi.CreateDCW('DISPLAY', device, None, None)
    if not dc:
        return {'create_dc_error': C.get_last_error()}
    try:
        count = U32(32768)
        name = C.create_unicode_buffer(count.value)
        ok = gdi.GetICMProfileW(dc, C.byref(count), name)
        return {'success': bool(ok), 'profile': profile_metadata(name.value)} if ok else {'error': C.get_last_error()}
    finally:
        gdi.DeleteDC(dc)

def wcs_profiles(device):
    per_user = W.BOOL()
    ok = wcs.WcsGetUsePerUserProfiles(device, int.from_bytes(b'mntr', 'big'), C.byref(per_user))
    result = {'device': device, 'per_user': bool(per_user.value) if ok else None,
              'per_user_error': None if ok else C.get_last_error()}
    for scope, label in [(0, 'system'), (1, 'user')]:
        size = U32()
        ok = wcs.WcsGetDefaultColorProfileSize(scope, device, 0, 4, 0, C.byref(size))
        if ok:
            name = C.create_unicode_buffer(size.value // 2 + 1)
            ok = wcs.WcsGetDefaultColorProfile(scope, device, 0, 4, 0, size.value, name)
        result[label] = profile_metadata(name.value) if ok else {'error': C.get_last_error()}
    return result

def devices():
    out = []
    for i in range(64):
        d = Device(cb=C.sizeof(Device))
        if not user.EnumDisplayDevicesW(None, i, C.byref(d), 0):
            break
        if not d.flags & 1:
            continue
        item = {key: getattr(d, key) for key in ['name', 'description', 'flags', 'device_id', 'key']}
        item['icm'] = icm(d.name)
        item['wcs'] = wcs_profiles(d.name)
        item['monitors'] = []
        for j in range(32):
            m = Device(cb=C.sizeof(Device))
            if not user.EnumDisplayDevicesW(d.name, j, C.byref(m), 1):
                break
            mi = {key: getattr(m, key) for key in ['name', 'description', 'flags', 'device_id', 'key']}
            mi['wcs'] = wcs_profiles(m.name)
            registry_key = m.key.removeprefix('\\Registry\\Machine\\')
            try:
                with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, registry_key, 0, winreg.KEY_READ) as key:
                    mi['registry_icm_profile'] = winreg.QueryValueEx(key, 'ICMProfile')[0]
            except OSError as ex:
                mi['registry_icm_profile_error'] = ex.winerror
            item['monitors'].append(mi)
        out.append(item)
    return out

def query_paths():
    assert C.sizeof(DisplayPath) == 72 and C.sizeof(Header) == 20
    for retry in range(3):
        paths, modes = U32(), U32()
        status = user.GetDisplayConfigBufferSizes(2, C.byref(paths), C.byref(modes))
        if status:
            return {'error': status}
        p = (DisplayPath * paths.value)()
        mode_buffer = C.create_string_buffer(modes.value * 64)
        status = user.QueryDisplayConfig(2, C.byref(paths), p, C.byref(modes), mode_buffer, None)
        if status != 122:
            break
    if status:
        return {'error': status}
    out = []
    for path in p[:paths.value]:
        row = {}
        scope = U32()
        hr = wcs.ColorProfileGetDisplayUserScope(path.target.adapter, path.source.id, C.byref(scope))
        row['profile_scope'] = {'hresult': hex(hr & 0xffffffff), 'scope': scope.value if hr == 0 else None}
        row['display_default'] = {}
        for s, label in [(0,'system'), (1,'user')]:
            profile = C.c_void_p()
            hr = wcs.ColorProfileGetDisplayDefault(s, path.target.adapter, path.source.id, 0, 7, C.byref(profile))
            row['display_default'][label] = {'hresult': hex(hr & 0xffffffff)}
            if profile.value:
                try:
                    row['display_default'][label]['profile'] = profile_metadata(C.wstring_at(profile))
                finally:
                    kernel.LocalFree(profile)
        for cls, kind, label, end in [(SourceName,1,'source',path.source), (TargetName,2,'target',path.target),
                                      (Advanced,9,'advanced',path.target), (Advanced2,15,'advanced2',path.target)]:
            value = cls()
            value.header = Header(kind, C.sizeof(cls), end.adapter, end.id)
            error = user.DisplayConfigGetDeviceInfo(C.byref(value))
            if error:
                row[label] = {'error': error}
            elif kind <= 2:
                row[label] = {'name': value.name}
                if kind == 2:
                    row[label]['path'] = value.path
            else:
                row[label] = {'flags': value.flags, 'supported': bool(value.flags & 1),
                              'enabled': bool(value.flags & 2), 'bits': value.bits, 'encoding': value.encoding}
                if kind == 15:
                    row[label]['active_mode'] = value.mode
        out.append(row)
    return out

def registry():
    rows = []
    # Explicit ICM/display-association keys only; no general user registry dump.
    for hive, label, path in [
        (winreg.HKEY_CURRENT_USER,'HKCU',r'Software\Microsoft\Windows NT\CurrentVersion\ICM'),
        (winreg.HKEY_LOCAL_MACHINE,'HKLM',r'SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM'),
        (winreg.HKEY_CURRENT_USER,'HKCU',r'SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM\ProfileAssociations\Display'),
    ]:
        def visit(rel, depth=0):
            try:
                with winreg.OpenKey(hive, rel, 0, winreg.KEY_READ) as k:
                    count, values, _ = winreg.QueryInfoKey(k)
                    data = {winreg.EnumValue(k,i)[0]: winreg.EnumValue(k,i)[1] for i in range(values)}
                    if data:
                        rows.append({'hive': label, 'key': rel, 'values': data})
                    if depth < 5:
                        for i in range(count):
                            visit(rel + '\\' + winreg.EnumKey(k,i),depth+1)
            except OSError as ex:
                rows.append({'hive': label, 'key': rel, 'error': ex.winerror})
        visit(path)
    return rows

if __name__ == '__main__':
    result = {'read_only': True, 'devices': devices(), 'display_paths': query_paths(), 'registry': registry()}
    text = json.dumps(result, indent=2, default=lambda v: v.hex() if isinstance(v,bytes) else str(v))
    if len(sys.argv)>1:
        Path(sys.argv[1]).write_text(text,encoding='utf-8')
    print(text)
