/*
 * UxPlay's crypto.h without OpenSSL: Ed25519, X25519 and SHA-512 from Monocypher, AES from the
 * system (CNG on Windows, CommonCrypto on macOS, where the receiver is tested), and MD5 here.
 * The behaviour matches UxPlay's OpenSSL version: CTR and CBC keep their state between calls
 * until reset, and keys made from a device id are the same ones UxPlay makes.
 *
 * GCM is only used by PIN pairing, which openbox doesn't offer.
 *
 * openbox is free software: GPL-3.0-or-later.
 */

#include <assert.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "crypto.h"
#include "utils.h"
#include "monocypher.h"
#include "monocypher-ed25519.h"

#ifdef _WIN32
#include <windows.h>
#include <bcrypt.h>
#else
#include <CommonCrypto/CommonCryptor.h>
#endif

#define SALT_PK "UxPlay-Persistent-Not-Secure-Public-Key"

int get_random_bytes(unsigned char *buf, int num) {
#ifdef _WIN32
    return BCryptGenRandom(NULL, buf, (ULONG) num, BCRYPT_USE_SYSTEM_PREFERRED_RNG) == 0;
#else
    arc4random_buf(buf, (size_t) num);
    return 1;
#endif
}

// AES-128, as blocks (ECB): CTR and CBC are built on it below.

typedef struct {
#ifdef _WIN32
    BCRYPT_KEY_HANDLE key;
#else
    CCCryptorRef encryptor;
    CCCryptorRef decryptor;
#endif
} aes_block_t;

static void aes_block_init(aes_block_t *block, const uint8_t *key) {
#ifdef _WIN32
    NTSTATUS status = BCryptGenerateSymmetricKey(BCRYPT_AES_ECB_ALG_HANDLE, &block->key, NULL, 0,
                                                 (PUCHAR) key, AES_128_BLOCK_SIZE, 0);
    assert(status == 0);
    (void) status;
#else
    CCCryptorCreate(kCCEncrypt, kCCAlgorithmAES, kCCOptionECBMode, key, AES_128_BLOCK_SIZE, NULL, &block->encryptor);
    CCCryptorCreate(kCCDecrypt, kCCAlgorithmAES, kCCOptionECBMode, key, AES_128_BLOCK_SIZE, NULL, &block->decryptor);
#endif
}

/* len is a whole number of blocks. */
static void aes_block_run(aes_block_t *block, bool encrypt, const uint8_t *in, uint8_t *out, size_t len) {
#ifdef _WIN32
    ULONG done = 0;
    if (encrypt) {
        BCryptEncrypt(block->key, (PUCHAR) in, (ULONG) len, NULL, NULL, 0, out, (ULONG) len, &done, 0);
    } else {
        BCryptDecrypt(block->key, (PUCHAR) in, (ULONG) len, NULL, NULL, 0, out, (ULONG) len, &done, 0);
    }
#else
    size_t done = 0;
    CCCryptorUpdate(encrypt ? block->encryptor : block->decryptor, in, len, out, len, &done);
#endif
}

static void aes_block_destroy(aes_block_t *block) {
#ifdef _WIN32
    BCryptDestroyKey(block->key);
#else
    CCCryptorRelease(block->encryptor);
    CCCryptorRelease(block->decryptor);
#endif
}

struct aes_ctx_s {
    aes_block_t block;
    uint8_t iv[AES_128_BLOCK_SIZE];
    aes_direction_t direction;
    /* CTR: the next counter block, and what's left of the current block's key stream. */
    uint8_t counter[AES_128_BLOCK_SIZE];
    uint8_t stream[AES_128_BLOCK_SIZE];
    uint8_t block_offset;
    /* CBC: the previous ciphertext block. */
    uint8_t chain[AES_128_BLOCK_SIZE];
};

static aes_ctx_t *aes_init(const uint8_t *key, const uint8_t *iv, aes_direction_t direction) {
    aes_ctx_t *ctx = (aes_ctx_t *) calloc(1, sizeof(aes_ctx_t));
    assert(ctx != NULL);
    aes_block_init(&ctx->block, key);
    memcpy(ctx->iv, iv, AES_128_BLOCK_SIZE);
    memcpy(ctx->counter, iv, AES_128_BLOCK_SIZE);
    memcpy(ctx->chain, iv, AES_128_BLOCK_SIZE);
    ctx->direction = direction;
    return ctx;
}

static void aes_destroy(aes_ctx_t *ctx) {
    if (ctx) {
        aes_block_destroy(&ctx->block);
        free(ctx);
    }
}

// AES CTR

static void counter_increment(uint8_t counter[AES_128_BLOCK_SIZE]) {
    for (int i = AES_128_BLOCK_SIZE - 1; i >= 0; i--) {
        if (++counter[i]) {
            break;
        }
    }
}

aes_ctx_t *aes_ctr_init(const uint8_t *key, const uint8_t *iv) {
    return aes_init(key, iv, AES_ENCRYPT);
}

void aes_ctr_encrypt(aes_ctx_t *ctx, const uint8_t *in, uint8_t *out, int len) {
    int i = 0;
    /* The rest of a block started by the last call. */
    while (ctx->block_offset && i < len) {
        out[i] = in[i] ^ ctx->stream[ctx->block_offset];
        ctx->block_offset = (ctx->block_offset + 1) % AES_128_BLOCK_SIZE;
        i++;
    }
    /* Whole blocks, with their key stream made in one call (the video stream is mostly this). */
    int blocks = (len - i) / AES_128_BLOCK_SIZE;
    if (blocks > 0) {
        uint8_t local[64 * AES_128_BLOCK_SIZE];
        while (blocks > 0) {
            int batch = blocks < 64 ? blocks : 64;
            for (int b = 0; b < batch; b++) {
                memcpy(local + b * AES_128_BLOCK_SIZE, ctx->counter, AES_128_BLOCK_SIZE);
                counter_increment(ctx->counter);
            }
            aes_block_run(&ctx->block, true, local, local, (size_t) batch * AES_128_BLOCK_SIZE);
            for (int k = 0; k < batch * AES_128_BLOCK_SIZE; k++, i++) {
                out[i] = in[i] ^ local[k];
            }
            blocks -= batch;
        }
    }
    /* The start of a block the next call continues. */
    if (i < len) {
        aes_block_run(&ctx->block, true, ctx->counter, ctx->stream, AES_128_BLOCK_SIZE);
        counter_increment(ctx->counter);
        while (i < len) {
            out[i] = in[i] ^ ctx->stream[ctx->block_offset++];
            i++;
        }
    }
}

void aes_ctr_start_fresh_block(aes_ctx_t *ctx) {
    ctx->block_offset = 0;
}

void aes_ctr_decrypt(aes_ctx_t *ctx, const uint8_t *in, uint8_t *out, int len) {
    aes_ctr_encrypt(ctx, in, out, len);
}

void aes_ctr_reset(aes_ctx_t *ctx) {
    memcpy(ctx->counter, ctx->iv, AES_128_BLOCK_SIZE);
    ctx->block_offset = 0;
}

void aes_ctr_destroy(aes_ctx_t *ctx) {
    aes_destroy(ctx);
}

// AES CBC, without padding: only whole blocks are processed, as with OpenSSL.

aes_ctx_t *aes_cbc_init(const uint8_t *key, const uint8_t *iv, aes_direction_t direction) {
    return aes_init(key, iv, direction);
}

void aes_cbc_encrypt(aes_ctx_t *ctx, const uint8_t *in, uint8_t *out, int len) {
    assert(ctx->direction == AES_ENCRYPT);
    for (int i = 0; i + AES_128_BLOCK_SIZE <= len; i += AES_128_BLOCK_SIZE) {
        uint8_t block[AES_128_BLOCK_SIZE];
        for (int k = 0; k < AES_128_BLOCK_SIZE; k++) {
            block[k] = in[i + k] ^ ctx->chain[k];
        }
        aes_block_run(&ctx->block, true, block, out + i, AES_128_BLOCK_SIZE);
        memcpy(ctx->chain, out + i, AES_128_BLOCK_SIZE);
    }
}

void aes_cbc_decrypt(aes_ctx_t *ctx, const uint8_t *in, uint8_t *out, int len) {
    assert(ctx->direction == AES_DECRYPT);
    int whole = len / AES_128_BLOCK_SIZE * AES_128_BLOCK_SIZE;
    if (whole <= 0) {
        return;
    }
    uint8_t *plain = (uint8_t *) malloc(whole);
    assert(plain != NULL);
    aes_block_run(&ctx->block, false, in, plain, whole);
    for (int i = 0; i < whole; i += AES_128_BLOCK_SIZE) {
        uint8_t next_chain[AES_128_BLOCK_SIZE];
        memcpy(next_chain, in + i, AES_128_BLOCK_SIZE);
        for (int k = 0; k < AES_128_BLOCK_SIZE; k++) {
            out[i + k] = plain[i + k] ^ ctx->chain[k];
        }
        memcpy(ctx->chain, next_chain, AES_128_BLOCK_SIZE);
    }
    free(plain);
}

void aes_cbc_reset(aes_ctx_t *ctx) {
    memcpy(ctx->chain, ctx->iv, AES_128_BLOCK_SIZE);
}

void aes_cbc_destroy(aes_ctx_t *ctx) {
    aes_destroy(ctx);
}

// GCM: not used without PIN pairing.

int gcm_encrypt(const unsigned char *plaintext, int plaintext_len, unsigned char *ciphertext,
                unsigned char *key, unsigned char *iv, unsigned char *tag) {
    (void) plaintext; (void) plaintext_len; (void) ciphertext; (void) key; (void) iv; (void) tag;
    return -1;
}

int gcm_decrypt(unsigned char *ciphertext, int ciphertext_len, unsigned char *plaintext,
                unsigned char *key, unsigned char *iv, unsigned char *tag) {
    (void) ciphertext; (void) ciphertext_len; (void) plaintext; (void) key; (void) iv; (void) tag;
    return -1;
}

// X25519

struct x25519_key_s {
    bool has_secret;
    uint8_t secret[X25519_KEY_SIZE];
    uint8_t public_key[X25519_KEY_SIZE];
};

x25519_key_t *x25519_key_generate(void) {
    x25519_key_t *key = (x25519_key_t *) calloc(1, sizeof(x25519_key_t));
    assert(key);
    get_random_bytes(key->secret, X25519_KEY_SIZE);
    crypto_x25519_public_key(key->public_key, key->secret);
    key->has_secret = true;
    return key;
}

x25519_key_t *x25519_key_from_raw(const unsigned char data[X25519_KEY_SIZE]) {
    x25519_key_t *key = (x25519_key_t *) calloc(1, sizeof(x25519_key_t));
    assert(key);
    memcpy(key->public_key, data, X25519_KEY_SIZE);
    return key;
}

void x25519_key_get_raw(unsigned char data[X25519_KEY_SIZE], const x25519_key_t *key) {
    assert(key);
    memcpy(data, key->public_key, X25519_KEY_SIZE);
}

void x25519_key_destroy(x25519_key_t *key) {
    if (key) {
        crypto_wipe(key, sizeof(*key));
        free(key);
    }
}

void x25519_derive_secret(unsigned char secret[X25519_KEY_SIZE], const x25519_key_t *ours, const x25519_key_t *theirs) {
    assert(ours && ours->has_secret);
    assert(theirs);
    crypto_x25519(secret, ours->secret, theirs->public_key);
}

// ED25519

struct ed25519_key_s {
    bool has_secret;
    /* Monocypher's secret key: the 32-byte seed, then the public key. */
    uint8_t secret[64];
    uint8_t public_key[ED25519_KEY_SIZE];
    int refs;
};

static ed25519_key_t *ed25519_key_from_seed(uint8_t seed[32]) {
    ed25519_key_t *key = (ed25519_key_t *) calloc(1, sizeof(ed25519_key_t));
    assert(key);
    crypto_ed25519_key_pair(key->secret, key->public_key, seed); /* wipes seed */
    key->has_secret = true;
    key->refs = 1;
    return key;
}

ed25519_key_t *ed25519_key_generate(const char *device_id, const char *keyfile, int *result) {
    uint8_t seed[32];
    *result = 0;
    if (keyfile && strlen(keyfile)) {
        /* The key's 32-byte seed, raw (UxPlay keeps a PEM file instead). */
        FILE *file = fopen(keyfile, "rb");
        bool loaded = false;
        if (file) {
            loaded = fread(seed, 1, sizeof(seed), file) == sizeof(seed);
            fclose(file);
        }
        if (!loaded) {
            get_random_bytes(seed, sizeof(seed));
            file = fopen(keyfile, "wb");
            if (file) {
                fwrite(seed, 1, sizeof(seed), file);
                fclose(file);
                *result = 1;
            }
        }
        return ed25519_key_from_seed(seed);
    }
    /* A persistent key made from the device id, as UxPlay does. */
    uint8_t hash[64];
    crypto_sha512_ctx ctx;
    crypto_sha512_init(&ctx);
    crypto_sha512_update(&ctx, (const uint8_t *) SALT_PK, strlen(SALT_PK));
    crypto_sha512_update(&ctx, (const uint8_t *) device_id, strlen(device_id));
    crypto_sha512_final(&ctx, hash);
    memcpy(seed, hash, sizeof(seed));
    return ed25519_key_from_seed(seed);
}

ed25519_key_t *ed25519_key_from_raw(const unsigned char data[ED25519_KEY_SIZE]) {
    ed25519_key_t *key = (ed25519_key_t *) calloc(1, sizeof(ed25519_key_t));
    assert(key);
    memcpy(key->public_key, data, ED25519_KEY_SIZE);
    key->refs = 1;
    return key;
}

void ed25519_key_get_raw(unsigned char data[ED25519_KEY_SIZE], const ed25519_key_t *key) {
    assert(key);
    memcpy(data, key->public_key, ED25519_KEY_SIZE);
}

ed25519_key_t *ed25519_key_copy(const ed25519_key_t *key) {
    assert(key);
    ed25519_key_t *copy = (ed25519_key_t *) malloc(sizeof(ed25519_key_t));
    assert(copy);
    memcpy(copy, key, sizeof(ed25519_key_t));
    return copy;
}

void ed25519_sign(unsigned char *signature, size_t signature_len,
                  const unsigned char *data, size_t data_len,
                  const ed25519_key_t *key) {
    assert(key && key->has_secret && signature_len >= 64);
    (void) signature_len;
    crypto_ed25519_sign(signature, key->secret, data, data_len);
}

int ed25519_verify(const unsigned char *signature, size_t signature_len,
                   const unsigned char *data, size_t data_len,
                   const ed25519_key_t *key) {
    if (signature_len != 64) {
        return 0;
    }
    /* 1 when it checks out, as EVP_DigestVerify. */
    return crypto_ed25519_check(signature, key->public_key, data, data_len) == 0;
}

void ed25519_key_destroy(ed25519_key_t *key) {
    if (key) {
        crypto_wipe(key, sizeof(*key));
        free(key);
    }
}

// SHA 512

struct sha_ctx_s {
    crypto_sha512_ctx ctx;
};

sha_ctx_t *sha_init() {
    sha_ctx_t *ctx = (sha_ctx_t *) calloc(1, sizeof(sha_ctx_t));
    assert(ctx != NULL);
    crypto_sha512_init(&ctx->ctx);
    return ctx;
}

void sha_update(sha_ctx_t *ctx, const uint8_t *in, int len) {
    crypto_sha512_update(&ctx->ctx, in, (size_t) len);
}

void sha_final(sha_ctx_t *ctx, uint8_t *out, unsigned int *len) {
    crypto_sha512_final(&ctx->ctx, out);
    if (len) {
        *len = 64;
    }
}

void sha_reset(sha_ctx_t *ctx) {
    crypto_sha512_init(&ctx->ctx);
}

void sha_destroy(sha_ctx_t *ctx) {
    free(ctx);
}

// MD5 (RFC 1321), for HTTP digest authentication.

struct md5_ctx_s {
    uint32_t state[4];
    uint64_t length;
    uint8_t buffer[64];
};

#define MD5_F(x, y, z) (((x) & (y)) | (~(x) & (z)))
#define MD5_G(x, y, z) (((x) & (z)) | ((y) & ~(z)))
#define MD5_H(x, y, z) ((x) ^ (y) ^ (z))
#define MD5_I(x, y, z) ((y) ^ ((x) | ~(z)))
#define MD5_ROTL(x, n) (((x) << (n)) | ((x) >> (32 - (n))))

static void md5_transform(uint32_t state[4], const uint8_t block[64]) {
    static const uint32_t k[64] = {
        0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee, 0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
        0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be, 0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
        0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa, 0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
        0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed, 0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
        0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c, 0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
        0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05, 0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
        0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039, 0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
        0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1, 0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391,
    };
    static const int r[64] = {
        7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
        5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
        4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
        6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
    };
    uint32_t w[16];
    for (int i = 0; i < 16; i++) {
        w[i] = (uint32_t) block[i * 4] | ((uint32_t) block[i * 4 + 1] << 8) |
               ((uint32_t) block[i * 4 + 2] << 16) | ((uint32_t) block[i * 4 + 3] << 24);
    }
    uint32_t a = state[0], b = state[1], c = state[2], d = state[3];
    for (int i = 0; i < 64; i++) {
        uint32_t f;
        int g;
        if (i < 16) { f = MD5_F(b, c, d); g = i; }
        else if (i < 32) { f = MD5_G(b, c, d); g = (5 * i + 1) % 16; }
        else if (i < 48) { f = MD5_H(b, c, d); g = (3 * i + 5) % 16; }
        else { f = MD5_I(b, c, d); g = (7 * i) % 16; }
        uint32_t next = d;
        d = c;
        c = b;
        b = b + MD5_ROTL(a + f + k[i] + w[g], r[i]);
        a = next;
    }
    state[0] += a;
    state[1] += b;
    state[2] += c;
    state[3] += d;
}

md5_ctx_t *md5_init() {
    md5_ctx_t *ctx = (md5_ctx_t *) calloc(1, sizeof(md5_ctx_t));
    assert(ctx != NULL);
    md5_reset(ctx);
    return ctx;
}

void md5_update(md5_ctx_t *ctx, const uint8_t *in, int len) {
    for (int i = 0; i < len; i++) {
        ctx->buffer[ctx->length % 64] = in[i];
        ctx->length++;
        if (ctx->length % 64 == 0) {
            md5_transform(ctx->state, ctx->buffer);
        }
    }
}

void md5_final(md5_ctx_t *ctx, uint8_t *out, unsigned int *len) {
    uint64_t bits = ctx->length * 8;
    uint8_t pad = 0x80;
    md5_update(ctx, &pad, 1);
    pad = 0;
    while (ctx->length % 64 != 56) {
        md5_update(ctx, &pad, 1);
    }
    uint8_t length_bytes[8];
    for (int i = 0; i < 8; i++) {
        length_bytes[i] = (uint8_t) (bits >> (8 * i));
    }
    md5_update(ctx, length_bytes, 8);
    for (int i = 0; i < 16; i++) {
        out[i] = (uint8_t) (ctx->state[i / 4] >> (8 * (i % 4)));
    }
    if (len) {
        *len = 16;
    }
}

void md5_reset(md5_ctx_t *ctx) {
    ctx->state[0] = 0x67452301;
    ctx->state[1] = 0xefcdab89;
    ctx->state[2] = 0x98badcfe;
    ctx->state[3] = 0x10325476;
    ctx->length = 0;
}

void md5_destroy(md5_ctx_t *ctx) {
    free(ctx);
}

char *get_md5(char *string) {
    unsigned char hash[16] = {0};
    md5_ctx_t *ctx = md5_init();
    md5_update(ctx, (const unsigned char *) string, (int) strlen(string));
    md5_final(ctx, hash, NULL);
    md5_destroy(ctx);
    return utils_hex_to_string(hash, 16); /* must be freed */
}

void pk_to_base64(const unsigned char *pk, int pk_len, char *pk_base64, int len) {
    static const char alphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    int len64 = (4 * (pk_len / 3)) + (pk_len % 3 ? 4 : 0);
    assert(len > len64);
    memset(pk_base64, 0, len);
    int n = 0;
    for (int i = 0; i < pk_len; i += 3) {
        uint32_t v = (uint32_t) pk[i] << 16;
        if (i + 1 < pk_len) v |= (uint32_t) pk[i + 1] << 8;
        if (i + 2 < pk_len) v |= pk[i + 2];
        pk_base64[n++] = alphabet[(v >> 18) & 63];
        pk_base64[n++] = alphabet[(v >> 12) & 63];
        pk_base64[n++] = i + 1 < pk_len ? alphabet[(v >> 6) & 63] : '=';
        pk_base64[n++] = i + 2 < pk_len ? alphabet[v & 63] : '=';
    }
}
