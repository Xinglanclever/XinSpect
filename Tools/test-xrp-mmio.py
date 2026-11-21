"""Exercise the real MMIO dispatch body with an overlapping METHOD_BUFFERED buffer.

No driver is loaded and no hardware is accessed. The native reader is a stub;
the dispatch and allow-list functions are extracted verbatim from the driver.
Run from a Visual Studio developer command prompt: python Tools/test-xrp-mmio.py
"""
import pathlib
import subprocess
import tempfile

root = pathlib.Path(__file__).resolve().parent.parent
source = (root / "XsRegProbe/Driver/XsRegProbe.c").read_text(encoding="utf-8-sig")


def function(name):
    start = source.index("static ", source.index(name) - 25)
    opening = source.index("{", source.index(name, start))
    depth = 1
    end = opening + 1
    while depth:
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    return source[start:end]


header = r'''
#include <stdint.h>
#include <stddef.h>
#include <stdio.h>
#include <string.h>
#ifndef _In_
#define _In_
#define _Out_
#define _Out_writes_bytes_(x)
#endif
#define TRUE 1
#define FALSE 0
#define STATUS_SUCCESS 0
#define STATUS_INVALID_PARAMETER ((int32_t)0xc000000d)
#define STATUS_ACCESS_DENIED ((int32_t)0xc0000022)
#define NT_SUCCESS(x) ((x) >= 0)
#define FIELD_OFFSET(type, member) offsetof(type, member)
#define RTL_NUMBER_OF(x) (sizeof(x) / sizeof((x)[0]))
typedef uint32_t ULONG;
typedef uint64_t ULONGLONG;
typedef uint8_t UCHAR;
typedef int BOOLEAN;
typedef int32_t NTSTATUS;
#include "XrpContract.h"
static ULONGLONG observed;
static int reads;
'''
reader = r'''
static NTSTATUS XrpReadMmioBlock(ULONGLONG address, ULONG length, UCHAR *out) {
    if (!XrpMmioRangeAllowed(address, length)) return STATUS_ACCESS_DENIED;
    observed = address;
    reads++;
    memset(out, 0x5a, length);
    return STATUS_SUCCESS;
}
'''
main = r'''
static int check(ULONGLONG address, ULONG length, ULONG inLen, ULONG outLen, NTSTATUS expected) {
    union { ULONGLONG alignment; UCHAR bytes[sizeof(XRP_MMIO_REPLY) + 16]; } buffer;
    memset(&buffer, 0xa5, sizeof(buffer));
    XRP_MMIO_REQUEST *request = (XRP_MMIO_REQUEST*)buffer.bytes;
    XRP_MMIO_REPLY *reply = (XRP_MMIO_REPLY*)buffer.bytes;
    request->PhysicalAddress = address;
    request->Length = length;
    request->Reserved = 0;
    ULONG returned = 0;
    observed = 0; reads = 0;
    NTSTATUS status = XrpReadMmio(request, inLen, reply, outLen, &returned);
    if (status != expected || (expected != STATUS_SUCCESS && (reads || returned))) {
        printf("FAIL address=%llx length=%u status=%x expected=%x reads=%d\n",
            (unsigned long long)address, length, status, expected, reads);
        return 1;
    }
    if (expected == STATUS_SUCCESS) {
        if (observed != address || reply->Length != length || returned != 8 + length) return 1;
        for (ULONG i=0; i<length; i++) if (reply->Data[i] != 0x5a) return 1;
    }
    for (size_t i=sizeof(XRP_MMIO_REPLY); i<sizeof(buffer.bytes); i++)
        if (buffer.bytes[i] != 0xa5) return 1;
    return 0;
}
int main(void) {
    int failures = 0;
    failures += check(0xfed10004ULL, 4, 16, 12, STATUS_SUCCESS);
    failures += check(0xe0000000ULL, 4096, 16, sizeof(XRP_MMIO_REPLY), STATUS_SUCCESS);
    failures += check(0x1fed10004ULL, 4, 16, 12, STATUS_ACCESS_DENIED);
    failures += check(0xfffffffffed10004ULL, 4, 16, 12, STATUS_ACCESS_DENIED);
    failures += check(0xfed10084ULL, 8, 16, 16, STATUS_ACCESS_DENIED);
    failures += check(0xfed10004ULL, 0, 16, 12, STATUS_INVALID_PARAMETER);
    failures += check(0xfed10004ULL, 4097, 16, sizeof(XRP_MMIO_REPLY), STATUS_INVALID_PARAMETER);
    failures += check(0xfed10004ULL, 4, 15, 12, STATUS_INVALID_PARAMETER);
    failures += check(0xfed10004ULL, 4, 16, 11, STATUS_INVALID_PARAMETER);
    printf("MMIO overlapping-buffer tests: 9 cases, %d failures\n", failures);
    return failures != 0;
}
'''

with tempfile.TemporaryDirectory(prefix="xinspect-mmio-test-") as directory:
    path = pathlib.Path(directory)
    c = path / "mmio.c"
    c.write_text(header + function("XrpMmioRangeAllowed") + reader
                 + function("XrpReadMmio(") + main, encoding="utf-8")
    subprocess.run(["cl", "/nologo", "/W4", "/WX", "/utf-8", "/TC", str(c),
                    "/I" + str(root / "XsRegProbe/Driver"), "/Fe:" + str(path / "mmio.exe")],
                   cwd=directory, check=True)
    subprocess.run([str(path / "mmio.exe")], check=True)
