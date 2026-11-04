/*
 * Included before every file of the receiver on Windows (/FI): the POSIX bits UxPlay uses that
 * MSVC doesn't have. Threads are in pthread.h, next to this.
 *
 * openbox is free software: GPL-3.0-or-later.
 */

#ifndef OPENBOX_WIN32_PORT_H
#define OPENBOX_WIN32_PORT_H

#ifndef WIN32
#define WIN32
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#define _CRT_SECURE_NO_WARNINGS
#define _WINSOCK_DEPRECATED_NO_WARNINGS

#include <stdlib.h>
#include <string.h>
#include <basetsd.h>

typedef SSIZE_T ssize_t;

#define strncasecmp _strnicmp
#define strcasecmp _stricmp
#define random() rand()
#define srandom(seed) srand(seed)

#endif
