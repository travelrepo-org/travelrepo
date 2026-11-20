/*
 * TravelRepo C ABI, for clients that compile TravelRepo with NativeAOT into a native library (TravelRepo.Native).
 *
 * Every function takes and returns UTF-8 strings that end with a zero byte. TravelRepo copies what it is given before
 * the function returns. A response passed to a callback is valid only while the callback runs; copy it to keep it.
 *
 * A call answers exactly once through its callback, on a TravelRepo thread, possibly before tr_call returns:
 *   {"ok":true,"result":...}
 *   {"ok":false,"error":{"code":"session.unknown","message":"..."}}
 * Never block the callback on the main thread of an app; hand the response over to it instead.
 *
 * Commands without a session (session 0): info, configure, open, clone.
 * Commands of a session: snapshot, sync_status, sync, add_remote, and the tools of the TravelRepo MCP server with the
 * same names and arguments (docs/mcp.md), such as get_trip, get_schedule, create_entity, update_entity, create_version.
 * See docs/native.md.
 */
#ifndef TRAVELREPO_H
#define TRAVELREPO_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef void (*tr_callback)(void *context, const char *response_json);
typedef void (*tr_credential_handler)(void *context, uint64_t request, const char *origin);

/* Starts a call. arguments_json is a JSON object or NULL. Returns the call id, or 0 when callback is NULL. */
uint64_t tr_call(uint64_t session, const char *command, const char *arguments_json, tr_callback callback, void *context);

/* Cancels a running call; it then answers with the error code call.cancelled. */
void tr_cancel(uint64_t call);

/* Closes a session that open returned. */
void tr_close(uint64_t session);

/* Sets the function Git asks for credentials (origin such as https://github.com); NULL removes it. */
void tr_set_credential_handler(tr_credential_handler handler, void *context);

/* Answers a credential request with {"username":"...","password":"..."}, or NULL for none. */
void tr_credential_reply(uint64_t request, const char *credential_json);

#ifdef __cplusplus
}
#endif

#endif
