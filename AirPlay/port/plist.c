/*
 * A small libplist for UxPlay (see plist/plist.h): nodes, binary plists ("bplist00") read and
 * written, and XML written. AirPlay clients only send binary plists.
 *
 * openbox is free software: GPL-3.0-or-later.
 */

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include "plist/plist.h"

typedef struct node_s {
    plist_type type;
    uint8_t b;
    uint64_t u;
    double r;
    /* String (UTF-8, terminated) or data bytes. */
    char *bytes;
    uint64_t length;
    /* Arrays and dicts: items, and for dicts each item's key. */
    struct node_s **items;
    char **keys;
    uint32_t count;
    uint32_t capacity;
} node_t;

static node_t *node_new(plist_type type) {
    node_t *node = (node_t *) calloc(1, sizeof(node_t));
    if (node) {
        node->type = type;
    }
    return node;
}

static char *copy_bytes(const char *bytes, uint64_t length) {
    char *copy = (char *) malloc((size_t) length + 1);
    if (copy) {
        if (length) {
            memcpy(copy, bytes, (size_t) length);
        }
        copy[length] = '\0';
    }
    return copy;
}

plist_t plist_new_dict(void) { return node_new(PLIST_DICT); }
plist_t plist_new_array(void) { return node_new(PLIST_ARRAY); }

plist_t plist_new_string(const char *val) {
    node_t *node = node_new(PLIST_STRING);
    if (node) {
        node->length = strlen(val);
        node->bytes = copy_bytes(val, node->length);
    }
    return node;
}

plist_t plist_new_bool(uint8_t val) {
    node_t *node = node_new(PLIST_BOOLEAN);
    if (node) {
        node->b = val ? 1 : 0;
    }
    return node;
}

plist_t plist_new_uint(uint64_t val) {
    node_t *node = node_new(PLIST_UINT);
    if (node) {
        node->u = val;
    }
    return node;
}

plist_t plist_new_int(int64_t val) {
    return plist_new_uint((uint64_t) val);
}

plist_t plist_new_real(double val) {
    node_t *node = node_new(PLIST_REAL);
    if (node) {
        node->r = val;
    }
    return node;
}

plist_t plist_new_data(const char *val, uint64_t length) {
    node_t *node = node_new(PLIST_DATA);
    if (node) {
        node->length = length;
        node->bytes = copy_bytes(val, length);
    }
    return node;
}

void plist_free(plist_t plist) {
    node_t *node = (node_t *) plist;
    if (!node) {
        return;
    }
    for (uint32_t i = 0; i < node->count; i++) {
        plist_free(node->items[i]);
        if (node->keys) {
            free(node->keys[i]);
        }
    }
    free(node->items);
    free(node->keys);
    free(node->bytes);
    free(node);
}

void plist_mem_free(void *ptr) {
    free(ptr);
}

plist_type plist_get_node_type(plist_t node) {
    return node ? ((node_t *) node)->type : PLIST_NONE;
}

static int append(node_t *node, char *key, node_t *item) {
    if (node->count == node->capacity) {
        uint32_t capacity = node->capacity ? node->capacity * 2 : 8;
        node_t **items = (node_t **) realloc(node->items, capacity * sizeof(node_t *));
        if (!items) {
            return -1;
        }
        node->items = items;
        if (node->type == PLIST_DICT) {
            char **keys = (char **) realloc(node->keys, capacity * sizeof(char *));
            if (!keys) {
                return -1;
            }
            node->keys = keys;
        }
        node->capacity = capacity;
    }
    node->items[node->count] = item;
    if (node->type == PLIST_DICT) {
        node->keys[node->count] = key;
    }
    node->count++;
    return 0;
}

uint32_t plist_array_get_size(plist_t plist) {
    node_t *node = (node_t *) plist;
    return node && node->type == PLIST_ARRAY ? node->count : 0;
}

plist_t plist_array_get_item(plist_t plist, uint32_t n) {
    node_t *node = (node_t *) plist;
    return node && node->type == PLIST_ARRAY && n < node->count ? node->items[n] : NULL;
}

void plist_array_append_item(plist_t plist, plist_t item) {
    node_t *node = (node_t *) plist;
    if (node && item && node->type == PLIST_ARRAY && append(node, NULL, (node_t *) item)) {
        plist_free(item);
    }
}

uint32_t plist_dict_get_size(plist_t plist) {
    node_t *node = (node_t *) plist;
    return node && node->type == PLIST_DICT ? node->count : 0;
}

plist_t plist_dict_get_item(plist_t plist, const char *key) {
    node_t *node = (node_t *) plist;
    if (!node || node->type != PLIST_DICT || !key) {
        return NULL;
    }
    for (uint32_t i = 0; i < node->count; i++) {
        if (!strcmp(node->keys[i], key)) {
            return node->items[i];
        }
    }
    return NULL;
}

void plist_dict_set_item(plist_t plist, const char *key, plist_t item) {
    node_t *node = (node_t *) plist;
    if (!node || node->type != PLIST_DICT || !key || !item) {
        plist_free(item);
        return;
    }
    for (uint32_t i = 0; i < node->count; i++) {
        if (!strcmp(node->keys[i], key)) {
            if (node->items[i] != item) {
                plist_free(node->items[i]);
                node->items[i] = (node_t *) item;
            }
            return;
        }
    }
    char *copy = copy_bytes(key, strlen(key));
    if (!copy || append(node, copy, (node_t *) item)) {
        free(copy);
        plist_free(item);
    }
}

void plist_get_string_val(plist_t plist, char **val) {
    node_t *node = (node_t *) plist;
    *val = node && node->type == PLIST_STRING ? copy_bytes(node->bytes, node->length) : NULL;
}

void plist_get_bool_val(plist_t plist, uint8_t *val) {
    node_t *node = (node_t *) plist;
    if (node && node->type == PLIST_BOOLEAN) {
        *val = node->b;
    }
}

void plist_get_uint_val(plist_t plist, uint64_t *val) {
    node_t *node = (node_t *) plist;
    if (node && (node->type == PLIST_UINT || node->type == PLIST_UID)) {
        *val = node->u;
    }
}

void plist_get_real_val(plist_t plist, double *val) {
    node_t *node = (node_t *) plist;
    if (node && (node->type == PLIST_REAL || node->type == PLIST_DATE)) {
        *val = node->r;
    }
}

void plist_get_data_val(plist_t plist, char **val, uint64_t *length) {
    node_t *node = (node_t *) plist;
    if (node && node->type == PLIST_DATA) {
        *val = copy_bytes(node->bytes, node->length);
        *length = node->length;
    } else {
        *val = NULL;
        *length = 0;
    }
}

const char *plist_get_data_ptr(plist_t plist, uint64_t *length) {
    node_t *node = (node_t *) plist;
    if (!node || node->type != PLIST_DATA) {
        *length = 0;
        return NULL;
    }
    *length = node->length;
    return node->bytes;
}

/* Reading binary plists. */

typedef struct {
    const unsigned char *data;
    uint64_t length;
    uint64_t *offsets;
    uint64_t object_count;
    int ref_size;
} reader_t;

static uint64_t read_be(const unsigned char *bytes, int size) {
    uint64_t value = 0;
    for (int i = 0; i < size; i++) {
        value = (value << 8) | bytes[i];
    }
    return value;
}

static double read_real(const unsigned char *bytes, int size) {
    uint64_t bits = read_be(bytes, size);
    if (size == 4) {
        uint32_t bits32 = (uint32_t) bits;
        float value;
        memcpy(&value, &bits32, 4);
        return value;
    }
    double value;
    memcpy(&value, &bits, 8);
    return value;
}

/* UTF-16BE to UTF-8. */
static char *utf16_to_utf8(const unsigned char *bytes, uint64_t units, uint64_t *out_length) {
    char *out = (char *) malloc((size_t) units * 3 + 1);
    uint64_t n = 0;
    if (!out) {
        return NULL;
    }
    for (uint64_t i = 0; i < units; i++) {
        uint32_t c = (uint32_t) read_be(bytes + i * 2, 2);
        if (c >= 0xD800 && c < 0xDC00 && i + 1 < units) {
            uint32_t low = (uint32_t) read_be(bytes + (i + 1) * 2, 2);
            if (low >= 0xDC00 && low < 0xE000) {
                c = 0x10000 + ((c - 0xD800) << 10) + (low - 0xDC00);
                i++;
            }
        }
        if (c < 0x80) {
            out[n++] = (char) c;
        } else if (c < 0x800) {
            out[n++] = (char) (0xC0 | (c >> 6));
            out[n++] = (char) (0x80 | (c & 0x3F));
        } else if (c < 0x10000) {
            out[n++] = (char) (0xE0 | (c >> 12));
            out[n++] = (char) (0x80 | ((c >> 6) & 0x3F));
            out[n++] = (char) (0x80 | (c & 0x3F));
        } else {
            out[n++] = (char) (0xF0 | (c >> 18));
            out[n++] = (char) (0x80 | ((c >> 12) & 0x3F));
            out[n++] = (char) (0x80 | ((c >> 6) & 0x3F));
            out[n++] = (char) (0x80 | (c & 0x3F));
        }
    }
    out[n] = '\0';
    *out_length = n;
    return out;
}

static node_t *read_object(reader_t *reader, uint64_t index, int depth);

/* An object's element count: in its marker's low nibble, or an int object after it. */
static int read_count(reader_t *reader, uint64_t *pos, uint64_t object_end, uint64_t *count) {
    unsigned char marker = reader->data[*pos];
    *pos += 1;
    if ((marker & 0x0F) != 0x0F) {
        *count = marker & 0x0F;
        return 0;
    }
    if (*pos >= object_end || (reader->data[*pos] & 0xF0) != 0x10) {
        return -1;
    }
    int size = 1 << (reader->data[*pos] & 0x0F);
    if (size > 8 || *pos + 1 + size > object_end) {
        return -1;
    }
    *count = read_be(reader->data + *pos + 1, size);
    *pos += 1 + size;
    return 0;
}

static node_t *read_object(reader_t *reader, uint64_t index, int depth) {
    if (index >= reader->object_count || depth > 64) {
        return NULL;
    }
    uint64_t pos = reader->offsets[index];
    /* Objects end where the offset table starts at the latest. */
    uint64_t end = reader->length - 32;
    if (pos >= end) {
        return NULL;
    }
    const unsigned char *data = reader->data;
    unsigned char marker = data[pos];
    unsigned char kind = marker & 0xF0;
    node_t *node = NULL;
    uint64_t count;

    switch (kind) {
    case 0x00:
        if (marker == 0x08 || marker == 0x09) {
            node = node_new(PLIST_BOOLEAN);
            if (node) node->b = marker == 0x09;
        } else {
            node = node_new(PLIST_NULL);
        }
        return node;
    case 0x10: {
        int size = 1 << (marker & 0x0F);
        if (size > 16 || pos + 1 + size > end) return NULL;
        node = node_new(PLIST_UINT);
        /* 16-byte ints: the value is in the low 8 bytes. */
        if (node) node->u = size == 16 ? read_be(data + pos + 9, 8) : read_be(data + pos + 1, size);
        return node;
    }
    case 0x20: {
        int size = 1 << (marker & 0x0F);
        if ((size != 4 && size != 8) || pos + 1 + size > end) return NULL;
        node = node_new(PLIST_REAL);
        if (node) node->r = read_real(data + pos + 1, size);
        return node;
    }
    case 0x30:
        if (marker != 0x33 || pos + 9 > end) return NULL;
        node = node_new(PLIST_DATE);
        if (node) node->r = read_real(data + pos + 1, 8);
        return node;
    case 0x40:
    case 0x50:
    case 0x60:
        if (read_count(reader, &pos, end, &count)) return NULL;
        if (kind == 0x60) {
            if (count > (end - pos) / 2) return NULL;
            node = node_new(PLIST_STRING);
            if (node) node->bytes = utf16_to_utf8(data + pos, count, &node->length);
        } else {
            if (count > end - pos) return NULL;
            node = node_new(kind == 0x40 ? PLIST_DATA : PLIST_STRING);
            if (node) {
                node->length = count;
                node->bytes = copy_bytes((const char *) data + pos, count);
            }
        }
        if (node && !node->bytes) {
            plist_free(node);
            node = NULL;
        }
        return node;
    case 0x80: {
        int size = (marker & 0x0F) + 1;
        if (size > 8 || pos + 1 + size > end) return NULL;
        node = node_new(PLIST_UID);
        if (node) node->u = read_be(data + pos + 1, size);
        return node;
    }
    case 0xA0:
    case 0xD0: {
        if (read_count(reader, &pos, end, &count)) return NULL;
        uint64_t refs = kind == 0xD0 ? count * 2 : count;
        if (count > end || refs > (end - pos) / reader->ref_size) return NULL;
        node = node_new(kind == 0xD0 ? PLIST_DICT : PLIST_ARRAY);
        if (!node) return NULL;
        for (uint64_t i = 0; i < count; i++) {
            uint64_t value_ref = read_be(data + pos + (kind == 0xD0 ? count + i : i) * reader->ref_size, reader->ref_size);
            node_t *value = read_object(reader, value_ref, depth + 1);
            if (!value) {
                plist_free(node);
                return NULL;
            }
            if (kind == 0xD0) {
                uint64_t key_ref = read_be(data + pos + i * reader->ref_size, reader->ref_size);
                node_t *key = read_object(reader, key_ref, depth + 1);
                if (!key || key->type != PLIST_STRING) {
                    plist_free(key);
                    plist_free(value);
                    plist_free(node);
                    return NULL;
                }
                plist_dict_set_item(node, key->bytes, value);
                plist_free(key);
            } else if (append(node, NULL, value)) {
                plist_free(value);
                plist_free(node);
                return NULL;
            }
        }
        return node;
    }
    default:
        return NULL;
    }
}

plist_err_t plist_from_bin(const char *plist_bin, uint32_t length, plist_t *plist) {
    reader_t reader;
    const unsigned char *data = (const unsigned char *) plist_bin;
    *plist = NULL;
    if (!plist_bin || length < 8 + 32 || memcmp(plist_bin, "bplist00", 8)) {
        return PLIST_ERR_PARSE;
    }
    const unsigned char *trailer = data + length - 32;
    int offset_size = trailer[6];
    reader.ref_size = trailer[7];
    reader.object_count = read_be(trailer + 8, 8);
    uint64_t top = read_be(trailer + 16, 8);
    uint64_t table = read_be(trailer + 24, 8);
    if (offset_size < 1 || offset_size > 8 || reader.ref_size < 1 || reader.ref_size > 8 ||
        reader.object_count == 0 || table < 8 || table >= length - 32 ||
        reader.object_count > (length - 32 - table) / offset_size) {
        return PLIST_ERR_PARSE;
    }
    reader.data = data;
    reader.length = length;
    reader.offsets = (uint64_t *) malloc((size_t) reader.object_count * sizeof(uint64_t));
    if (!reader.offsets) {
        return PLIST_ERR_NO_MEM;
    }
    for (uint64_t i = 0; i < reader.object_count; i++) {
        reader.offsets[i] = read_be(data + table + i * offset_size, offset_size);
    }
    *plist = read_object(&reader, top, 0);
    free(reader.offsets);
    return *plist ? PLIST_ERR_SUCCESS : PLIST_ERR_PARSE;
}

/* Writing. */

typedef struct {
    unsigned char *bytes;
    size_t length;
    size_t capacity;
    int failed;
} buffer_t;

static void put(buffer_t *buffer, const void *bytes, size_t length) {
    if (buffer->failed) {
        return;
    }
    if (buffer->length + length > buffer->capacity) {
        size_t capacity = buffer->capacity ? buffer->capacity : 256;
        while (capacity < buffer->length + length) {
            capacity *= 2;
        }
        unsigned char *grown = (unsigned char *) realloc(buffer->bytes, capacity);
        if (!grown) {
            buffer->failed = 1;
            return;
        }
        buffer->bytes = grown;
        buffer->capacity = capacity;
    }
    memcpy(buffer->bytes + buffer->length, bytes, length);
    buffer->length += length;
}

static void put_byte(buffer_t *buffer, unsigned char byte) {
    put(buffer, &byte, 1);
}

static void put_be(buffer_t *buffer, uint64_t value, int size) {
    unsigned char bytes[8];
    for (int i = size - 1; i >= 0; i--) {
        bytes[i] = (unsigned char) (value & 0xFF);
        value >>= 8;
    }
    put(buffer, bytes, size);
}

static void put_str(buffer_t *buffer, const char *text) {
    put(buffer, text, strlen(text));
}

static void put_int_object(buffer_t *buffer, uint64_t value) {
    if (value <= 0xFF) {
        put_byte(buffer, 0x10);
        put_be(buffer, value, 1);
    } else if (value <= 0xFFFF) {
        put_byte(buffer, 0x11);
        put_be(buffer, value, 2);
    } else if (value <= 0xFFFFFFFFu) {
        put_byte(buffer, 0x12);
        put_be(buffer, value, 4);
    } else if (value <= 0x7FFFFFFFFFFFFFFFull) {
        put_byte(buffer, 0x13);
        put_be(buffer, value, 8);
    } else {
        put_byte(buffer, 0x14);
        put_be(buffer, 0, 8);
        put_be(buffer, value, 8);
    }
}

static void put_marker(buffer_t *buffer, unsigned char kind, uint64_t count) {
    if (count < 15) {
        put_byte(buffer, (unsigned char) (kind | count));
    } else {
        put_byte(buffer, (unsigned char) (kind | 0x0F));
        put_int_object(buffer, count);
    }
}

static uint64_t count_objects(node_t *node) {
    uint64_t count = 1;
    for (uint32_t i = 0; i < node->count; i++) {
        count += count_objects(node->items[i]) + (node->type == PLIST_DICT ? 1 : 0);
    }
    return count;
}

static int is_ascii(const char *bytes, uint64_t length) {
    for (uint64_t i = 0; i < length; i++) {
        if ((unsigned char) bytes[i] >= 0x80) {
            return 0;
        }
    }
    return 1;
}

static void put_string_object(buffer_t *buffer, const char *text, uint64_t length) {
    if (is_ascii(text, length)) {
        put_marker(buffer, 0x50, length);
        put(buffer, text, (size_t) length);
        return;
    }
    /* UTF-8 to UTF-16BE. */
    buffer_t units = { 0 };
    uint64_t unit_count = 0;
    const unsigned char *s = (const unsigned char *) text;
    for (uint64_t i = 0; i < length;) {
        uint32_t c = s[i];
        int extra = c >= 0xF0 ? 3 : c >= 0xE0 ? 2 : c >= 0xC0 ? 1 : 0;
        c &= extra == 3 ? 0x07 : extra == 2 ? 0x0F : extra == 1 ? 0x1F : 0x7F;
        i++;
        for (int k = 0; k < extra && i < length; k++, i++) {
            c = (c << 6) | (s[i] & 0x3F);
        }
        if (c >= 0x10000) {
            c -= 0x10000;
            put_be(&units, 0xD800 + (c >> 10), 2);
            put_be(&units, 0xDC00 + (c & 0x3FF), 2);
            unit_count += 2;
        } else {
            put_be(&units, c, 2);
            unit_count++;
        }
    }
    put_marker(buffer, 0x60, unit_count);
    if (units.length) {
        put(buffer, units.bytes, units.length);
    }
    free(units.bytes);
}

typedef struct {
    buffer_t out;
    uint64_t *offsets;
    uint64_t next;
    int ref_size;
} writer_t;

/* Writes node and everything under it, depth first; returns node's object index. */
static uint64_t write_object(writer_t *writer, node_t *node) {
    uint64_t index = writer->next++;
    uint64_t *refs = NULL;
    uint64_t ref_count = 0;

    /* Children (and dict keys) are written first so their indexes are known; the container's
       own bytes come after them, at the offset recorded for its index. */
    if (node->type == PLIST_ARRAY || node->type == PLIST_DICT) {
        ref_count = node->type == PLIST_DICT ? (uint64_t) node->count * 2 : node->count;
        refs = (uint64_t *) calloc((size_t) (ref_count ? ref_count : 1), sizeof(uint64_t));
        if (!refs) {
            writer->out.failed = 1;
            return index;
        }
        for (uint32_t i = 0; i < node->count; i++) {
            if (node->type == PLIST_DICT) {
                uint64_t key_index = writer->next++;
                writer->offsets[key_index] = writer->out.length;
                put_string_object(&writer->out, node->keys[i], strlen(node->keys[i]));
                refs[i] = key_index;
                refs[node->count + i] = write_object(writer, node->items[i]);
            } else {
                refs[i] = write_object(writer, node->items[i]);
            }
        }
    }

    writer->offsets[index] = writer->out.length;
    switch (node->type) {
    case PLIST_BOOLEAN:
        put_byte(&writer->out, node->b ? 0x09 : 0x08);
        break;
    case PLIST_UINT:
        put_int_object(&writer->out, node->u);
        break;
    case PLIST_REAL:
    case PLIST_DATE: {
        uint64_t bits;
        memcpy(&bits, &node->r, 8);
        put_byte(&writer->out, node->type == PLIST_DATE ? 0x33 : 0x23);
        put_be(&writer->out, bits, 8);
        break;
    }
    case PLIST_STRING:
    case PLIST_KEY:
        put_string_object(&writer->out, node->bytes, node->length);
        break;
    case PLIST_DATA:
        put_marker(&writer->out, 0x40, node->length);
        put(&writer->out, node->bytes, (size_t) node->length);
        break;
    case PLIST_UID:
        put_byte(&writer->out, 0x87);
        put_be(&writer->out, node->u, 8);
        break;
    case PLIST_ARRAY:
    case PLIST_DICT:
        put_marker(&writer->out, node->type == PLIST_DICT ? 0xD0 : 0xA0, node->count);
        for (uint64_t i = 0; i < ref_count; i++) {
            put_be(&writer->out, refs[i], writer->ref_size);
        }
        break;
    default:
        put_byte(&writer->out, 0x00);
        break;
    }
    free(refs);
    return index;
}

plist_err_t plist_to_bin(plist_t plist, char **plist_bin, uint32_t *length) {
    node_t *root = (node_t *) plist;
    writer_t writer = { { 0 } };
    *plist_bin = NULL;
    *length = 0;
    if (!root) {
        return PLIST_ERR_INVALID_ARG;
    }
    uint64_t objects = count_objects(root);
    writer.ref_size = objects < 0x100 ? 1 : objects < 0x10000 ? 2 : 4;
    writer.offsets = (uint64_t *) malloc((size_t) objects * sizeof(uint64_t));
    if (!writer.offsets) {
        return PLIST_ERR_NO_MEM;
    }
    put(&writer.out, "bplist00", 8);
    uint64_t top = write_object(&writer, root);
    uint64_t table = writer.out.length;
    int offset_size = table < 0x100 ? 1 : table < 0x10000 ? 2 : table < 0x100000000ull ? 4 : 8;
    for (uint64_t i = 0; i < objects; i++) {
        put_be(&writer.out, writer.offsets[i], offset_size);
    }
    unsigned char trailer[6] = { 0 };
    put(&writer.out, trailer, 6);
    put_byte(&writer.out, (unsigned char) offset_size);
    put_byte(&writer.out, (unsigned char) writer.ref_size);
    put_be(&writer.out, objects, 8);
    put_be(&writer.out, top, 8);
    put_be(&writer.out, table, 8);
    free(writer.offsets);
    if (writer.out.failed) {
        free(writer.out.bytes);
        return PLIST_ERR_NO_MEM;
    }
    *plist_bin = (char *) writer.out.bytes;
    *length = (uint32_t) writer.out.length;
    return PLIST_ERR_SUCCESS;
}

static void put_escaped(buffer_t *buffer, const char *text, uint64_t length) {
    for (uint64_t i = 0; i < length; i++) {
        switch (text[i]) {
        case '<': put_str(buffer, "&lt;"); break;
        case '>': put_str(buffer, "&gt;"); break;
        case '&': put_str(buffer, "&amp;"); break;
        default: put_byte(buffer, (unsigned char) text[i]); break;
        }
    }
}

static void put_base64(buffer_t *buffer, const unsigned char *bytes, uint64_t length) {
    static const char alphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    for (uint64_t i = 0; i < length; i += 3) {
        uint32_t n = (uint32_t) bytes[i] << 16;
        if (i + 1 < length) n |= (uint32_t) bytes[i + 1] << 8;
        if (i + 2 < length) n |= bytes[i + 2];
        put_byte(buffer, alphabet[(n >> 18) & 63]);
        put_byte(buffer, alphabet[(n >> 12) & 63]);
        put_byte(buffer, i + 1 < length ? alphabet[(n >> 6) & 63] : '=');
        put_byte(buffer, i + 2 < length ? alphabet[n & 63] : '=');
    }
}

static void put_indent(buffer_t *buffer, int depth) {
    for (int i = 0; i < depth; i++) {
        put_byte(buffer, '\t');
    }
}

static void write_xml(buffer_t *buffer, node_t *node, int depth) {
    char number[64];
    put_indent(buffer, depth);
    switch (node->type) {
    case PLIST_BOOLEAN:
        put_str(buffer, node->b ? "<true/>\n" : "<false/>\n");
        break;
    case PLIST_UINT:
    case PLIST_UID:
        snprintf(number, sizeof(number), "<integer>%llu</integer>\n", (unsigned long long) node->u);
        put_str(buffer, number);
        break;
    case PLIST_REAL:
    case PLIST_DATE:
        snprintf(number, sizeof(number), "<real>%f</real>\n", node->r);
        put_str(buffer, number);
        break;
    case PLIST_STRING:
    case PLIST_KEY:
        put_str(buffer, "<string>");
        put_escaped(buffer, node->bytes, node->length);
        put_str(buffer, "</string>\n");
        break;
    case PLIST_DATA:
        /* As libplist lays it out, which UxPlay's log trimming (utils.c) relies on: the base64 on
           lines of its own, 64 characters each. */
        put_str(buffer, "<data>\n");
        for (uint64_t i = 0; i < node->length; i += 48) {
            put_indent(buffer, depth);
            put_base64(buffer, (const unsigned char *) node->bytes + i, node->length - i < 48 ? node->length - i : 48);
            put_byte(buffer, '\n');
        }
        put_indent(buffer, depth);
        put_str(buffer, "</data>\n");
        break;
    case PLIST_ARRAY:
    case PLIST_DICT:
        put_str(buffer, node->type == PLIST_DICT ? "<dict>\n" : "<array>\n");
        for (uint32_t i = 0; i < node->count; i++) {
            if (node->type == PLIST_DICT) {
                put_indent(buffer, depth + 1);
                put_str(buffer, "<key>");
                put_escaped(buffer, node->keys[i], strlen(node->keys[i]));
                put_str(buffer, "</key>\n");
            }
            write_xml(buffer, node->items[i], depth + 1);
        }
        put_indent(buffer, depth);
        put_str(buffer, node->type == PLIST_DICT ? "</dict>\n" : "</array>\n");
        break;
    default:
        put_str(buffer, "<string></string>\n");
        break;
    }
}

plist_err_t plist_to_xml(plist_t plist, char **plist_xml, uint32_t *length) {
    buffer_t buffer = { 0 };
    *plist_xml = NULL;
    *length = 0;
    if (!plist) {
        return PLIST_ERR_INVALID_ARG;
    }
    put_str(&buffer, "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
        "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n"
        "<plist version=\"1.0\">\n");
    write_xml(&buffer, (node_t *) plist, 0);
    put_str(&buffer, "</plist>\n");
    put_byte(&buffer, 0);
    if (buffer.failed) {
        free(buffer.bytes);
        return PLIST_ERR_NO_MEM;
    }
    *plist_xml = (char *) buffer.bytes;
    *length = (uint32_t) buffer.length - 1;
    return PLIST_ERR_SUCCESS;
}
