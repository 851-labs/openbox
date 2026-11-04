/*
 * The part of libplist's API (2.3) that UxPlay uses: building and reading nodes, binary plists
 * both ways, and XML out (UxPlay logs requests as XML). Written for openbox so the receiver needs
 * no libplist build on Windows; see plist.c.
 *
 * openbox is free software: GPL-3.0-or-later.
 */

#ifndef OPENBOX_PLIST_H
#define OPENBOX_PLIST_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* UxPlay checks these to pick the libplist 2.1 and 2.3 calls. */
#define PLIST_210
#define PLIST_230

typedef void *plist_t;

typedef enum {
    PLIST_BOOLEAN,
    PLIST_UINT,
    PLIST_REAL,
    PLIST_STRING,
    PLIST_ARRAY,
    PLIST_DICT,
    PLIST_DATE,
    PLIST_DATA,
    PLIST_KEY,
    PLIST_UID,
    PLIST_NULL,
    PLIST_NONE
} plist_type;

#define PLIST_INT PLIST_UINT

typedef enum {
    PLIST_ERR_SUCCESS = 0,
    PLIST_ERR_INVALID_ARG = -1,
    PLIST_ERR_FORMAT = -2,
    PLIST_ERR_PARSE = -3,
    PLIST_ERR_NO_MEM = -4,
    PLIST_ERR_UNKNOWN = -255
} plist_err_t;

plist_t plist_new_dict(void);
plist_t plist_new_array(void);
plist_t plist_new_string(const char *val);
plist_t plist_new_bool(uint8_t val);
plist_t plist_new_uint(uint64_t val);
plist_t plist_new_int(int64_t val);
plist_t plist_new_real(double val);
plist_t plist_new_data(const char *val, uint64_t length);
void plist_free(plist_t plist);
void plist_mem_free(void *ptr);

plist_type plist_get_node_type(plist_t node);

uint32_t plist_array_get_size(plist_t node);
plist_t plist_array_get_item(plist_t node, uint32_t n);
void plist_array_append_item(plist_t node, plist_t item);

uint32_t plist_dict_get_size(plist_t node);
plist_t plist_dict_get_item(plist_t node, const char *key);
/* Takes ownership of item, replacing (and freeing) any item already under key. */
void plist_dict_set_item(plist_t node, const char *key, plist_t item);

/* The _val getters copy: free the result with plist_mem_free. */
void plist_get_string_val(plist_t node, char **val);
void plist_get_bool_val(plist_t node, uint8_t *val);
void plist_get_uint_val(plist_t node, uint64_t *val);
void plist_get_real_val(plist_t node, double *val);
void plist_get_data_val(plist_t node, char **val, uint64_t *length);
const char *plist_get_data_ptr(plist_t node, uint64_t *length);

plist_err_t plist_to_bin(plist_t plist, char **plist_bin, uint32_t *length);
plist_err_t plist_to_xml(plist_t plist, char **plist_xml, uint32_t *length);
plist_err_t plist_from_bin(const char *plist_bin, uint32_t length, plist_t *plist);

#define PLIST_IS_TYPE(node, type) ((node) && plist_get_node_type(node) == (type))
#define PLIST_IS_BOOLEAN(node) PLIST_IS_TYPE(node, PLIST_BOOLEAN)
#define PLIST_IS_UINT(node) PLIST_IS_TYPE(node, PLIST_UINT)
#define PLIST_IS_INT(node) PLIST_IS_TYPE(node, PLIST_INT)
#define PLIST_IS_REAL(node) PLIST_IS_TYPE(node, PLIST_REAL)
#define PLIST_IS_STRING(node) PLIST_IS_TYPE(node, PLIST_STRING)
#define PLIST_IS_ARRAY(node) PLIST_IS_TYPE(node, PLIST_ARRAY)
#define PLIST_IS_DICT(node) PLIST_IS_TYPE(node, PLIST_DICT)
#define PLIST_IS_DATE(node) PLIST_IS_TYPE(node, PLIST_DATE)
#define PLIST_IS_DATA(node) PLIST_IS_TYPE(node, PLIST_DATA)

#ifdef __cplusplus
}
#endif

#endif
