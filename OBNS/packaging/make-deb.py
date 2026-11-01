#!/usr/bin/env python3
# OBNS: builds the Ubuntu package from a finished desktop build, without touching OpenBVE/. It takes upstream's Debian
# template (OpenBVE/installers/debian) as its Makefile does, with OBNS's control file: dpkg-deb isn't needed, so this runs
# on the PC that made the build. The program is the same .NET build on every system.
#
#   python OBNS/packaging/make-deb.py
#
# Output: OBNS/build/dist/OpenBVE-<version>.deb
import gzip
import hashlib
import io
import os
import re
import tarfile
import time

root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
template = os.path.join(root, 'OpenBVE', 'installers', 'debian')
build = os.path.join(root, 'OBNS', 'build', 'upstream', 'bin_release')
dist = os.path.join(root, 'OBNS', 'build', 'dist')
now = int(time.time())


def product_version():
    # OBNS's AssemblyInfo.tt holds the release, as the Windows package's name does.
    with open(os.path.join(root, 'OBNS', 'overrides', 'source', 'OpenBVE', 'Properties', 'AssemblyInfo.tt'), encoding='utf-8-sig') as f:
        return re.search(r'string version = "([^"]+)"', f.read()).group(1) + '-OBNS'


def control_text(version, installed_kib):
    return f"""Package: openbve-obns
Priority: optional
Section: games
Installed-Size: {installed_kib}
Maintainer: The OpenBVE Android port contributors <333285344+OZKiller-LLM@users.noreply.github.com>
Architecture: all
Version: {version}
Provides: bve-engine, openbve
Conflicts: openbve
Replaces: openbve
Depends: mono-complete (>= 5.20.1), libopenal1, libsdl2-2.0-0, libusb-1.0-0, fonts-noto-core, fonts-noto-cjk
Recommends: libvulkan1, bve-route, bve-train
Description: realistic 3D train/railway simulator, with OBNS's additions
 OpenBVE is a railway train-driving simulator with an emphasis on
 in-cab driving, realistic physics, braking system and train safety
 system modelling.
 .
 This build adds BVE5 trains and their safety plugins, a Vulkan renderer
 and a compatibility layer that runs 32-bit Windows train plugins on Linux.
"""


def files():
    """(path in the package, file on disk, mode) for everything under the package's root."""
    # Upstream's template: its launcher is executable, the rest plain.
    for folder, _, names in os.walk(template):
        relative = os.path.relpath(folder, template).replace(os.sep, '/')
        if relative == 'DEBIAN' or relative.startswith('DEBIAN/'):
            continue
        for name in names:
            path = (relative + '/' + name) if relative != '.' else name
            yield path, os.path.join(folder, name), 0o755 if path == 'usr/games/openbve' else 0o644
    # The program, without debug symbols or the build's own tools, as upstream's package.
    for folder, folders, names in os.walk(build):
        relative = os.path.relpath(folder, build).replace(os.sep, '/')
        if relative == '.':
            folders[:] = [f for f in folders if f != 'DevTools']
        for name in names:
            if name.endswith('.pdb'):
                continue
            inside = name if relative == '.' else relative + '/' + name
            # Upstream's Makefile marks Data 755: https://github.com/leezer3/OpenBVE/issues/656
            yield 'usr/lib/openbve/' + inside, os.path.join(folder, name), 0o755 if inside.startswith('Data/') else 0o644
    # BSD-2's second condition, and the libraries' own: a binary release carries the notices.
    yield 'usr/share/doc/openbve/LICENSE.OBNS', os.path.join(root, 'LICENSE'), 0o644
    licenses = os.path.join(root, 'OpenBVE', 'licenses')
    for name in sorted(os.listdir(licenses)):
        yield 'usr/share/doc/openbve/licenses/' + name, os.path.join(licenses, name), 0o644


def info(name, size, mode, kind=tarfile.REGTYPE):
    item = tarfile.TarInfo('./' + name if name else './')
    item.size, item.mode, item.type, item.mtime = size, mode, kind, now
    item.uid = item.gid = 0
    item.uname = item.gname = 'root'
    return item


def member(out, name, data):
    out.write(f'{name:<16}{now:<12}0     0     100644  {len(data):<10}`\n'.encode('ascii'))
    out.write(data)
    if len(data) % 2:
        out.write(b'\n')


def main():
    if not os.path.isfile(os.path.join(build, 'OpenBve.exe')):
        raise SystemExit('Build the desktop program first: ' + build)
    entries = sorted(files())
    if len({e[0] for e in entries}) != len(entries):
        raise SystemExit('Two files for one path in the package')
    release = product_version()                # 1.14.1.2-OBNS
    version = release.replace('-OBNS', '+obns')  # a hyphen would make "OBNS" a Debian revision
    data_stream, md5sums, folders, total = io.BytesIO(), [], set(), 0
    with gzip.GzipFile(fileobj=data_stream, mode='wb', mtime=now) as z, tarfile.open(fileobj=z, mode='w', format=tarfile.GNU_FORMAT) as tar:
        tar.addfile(info('', 0, 0o755, tarfile.DIRTYPE))
        for path, source, mode in entries:
            parts = path.split('/')
            for depth in range(1, len(parts)):
                folder = '/'.join(parts[:depth])
                if folder not in folders:
                    folders.add(folder)
                    tar.addfile(info(folder + '/', 0, 0o755, tarfile.DIRTYPE))
            with open(source, 'rb') as f:
                content = f.read()
            if not path.startswith('usr/lib/') and not path.endswith(('.gz', '.png')):
                content = content.replace(b'\r\n', b'\n')  # scripts and Linux's text files, whatever the checkout did to them
            total += len(content)
            md5sums.append(f'{hashlib.md5(content).hexdigest()}  {path}\n')
            tar.addfile(info(path, len(content), mode), io.BytesIO(content))
    control_stream = io.BytesIO()
    with gzip.GzipFile(fileobj=control_stream, mode='wb', mtime=now) as z, tarfile.open(fileobj=z, mode='w', format=tarfile.GNU_FORMAT) as tar:
        tar.addfile(info('', 0, 0o755, tarfile.DIRTYPE))
        def add(name, content, mode):
            tar.addfile(info(name, len(content), mode), io.BytesIO(content))
        add('control', control_text(version, (total + 1023) // 1024).encode('utf-8'), 0o644)
        add('md5sums', ''.join(md5sums).encode('ascii'), 0o644)
        for name, mode in (('conffiles', 0o644), ('postinst', 0o755)):
            with open(os.path.join(template, 'DEBIAN', name), 'rb') as f:
                add(name, f.read().replace(b'\r\n', b'\n'), mode)
    os.makedirs(dist, exist_ok=True)
    target = os.path.join(dist, f'OpenBVE-{release}.deb')
    with open(target, 'wb') as out:
        out.write(b'!<arch>\n')
        member(out, 'debian-binary', b'2.0\n')
        member(out, 'control.tar.gz', control_stream.getvalue())
        member(out, 'data.tar.gz', data_stream.getvalue())
    print(f'Packaged: {target} ({os.path.getsize(target) >> 20} MB), version {version}, {len(entries)} files')


main()
