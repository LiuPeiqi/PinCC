#ifndef PINCC_SQLITE_SHIM_H
#define PINCC_SQLITE_SHIM_H
#include <sqlite3.h>
// Keep SQLITE_TRANSIENT's function-pointer macro cast on the C side.
static inline int pincc_bind_text16(sqlite3_stmt *stmt, int index,
                                  const void *text, int bytes) {
    return sqlite3_bind_text16(stmt, index, text, bytes, SQLITE_TRANSIENT);
}
static inline int pincc_bind_blob(sqlite3_stmt *stmt, int index,
                                  const void *blob, int bytes) {
    return sqlite3_bind_blob(stmt, index, blob, bytes, SQLITE_TRANSIENT);
}
#endif
