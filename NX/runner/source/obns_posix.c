// OBNS 1.31N: POSIX behaviour .NET expects where the Switch's file system differs.
// Copyright (c) The OpenBVE Android port contributors. BSD-2-Clause, as OBNS.
//
// Linked with -Wl,--wrap=<name> (NX/runner/Makefile), so every call in the program, .NET's System.Native included,
// comes here first.

#include <errno.h>
#include <sys/stat.h>

int __real_mkdir(const char *path, mode_t mode);

// mkdir on a folder that exists fails on the SD card, and .NET's Directory.CreateDirectory then throws "IOException:
// Success" where POSIX would let it carry on (the first run, 2026-11-05). OpenBVE creates folders that are already
// there all the time. Setting EEXIST wasn't enough: .NET still read an errno of 0 after the call (to be looked into),
// so an existing folder is reported as made, which .NET treats the same way.
int __wrap_mkdir(const char *path, mode_t mode)
{
    int result = __real_mkdir(path, mode);
    if (result != 0) {
        int saved = errno;
        struct stat st;
        if (stat(path, &st) == 0 && S_ISDIR(st.st_mode)) {
            errno = 0;
            return 0;
        }
        errno = saved != 0 ? saved : ENOENT;
    }
    return result;
}
