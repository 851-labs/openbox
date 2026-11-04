/*
 * UxPlay's SRP (srp.h) is only for pairing with a PIN or password, which openbox doesn't
 * advertise. This stands in for it without OpenSSL's big numbers: every attempt fails, safely.
 *
 * openbox is free software: GPL-3.0-or-later.
 */

#include <stdlib.h>
#include "srp.h"

void srp_random_seed(const unsigned char *random_data, int data_length) {
    (void) random_data; (void) data_length;
}

void srp_create_salted_verification_key(SRP_HashAlgorithm alg, SRP_NGType ng_type, const char *username,
                                        const unsigned char *password, int len_password,
                                        const unsigned char **bytes_s, int *len_s,
                                        const unsigned char **bytes_v, int *len_v,
                                        const char *n_hex, const char *g_hex) {
    (void) alg; (void) ng_type; (void) username; (void) password; (void) len_password; (void) n_hex; (void) g_hex;
    *bytes_s = (const unsigned char *) calloc(1, 1);
    *bytes_v = (const unsigned char *) calloc(1, 1);
    *len_s = 0;
    *len_v = 0;
}

void srp_create_server_ephemeral_key(SRP_HashAlgorithm alg, SRP_NGType ng_type,
                                     const unsigned char *bytes_v, int len_v,
                                     const unsigned char *bytes_b, int len_b,
                                     const unsigned char **bytes_B, int *len_B,
                                     const char *n_hex, const char *g_hex, int rfc5054_compat) {
    (void) alg; (void) ng_type; (void) bytes_v; (void) len_v; (void) bytes_b; (void) len_b;
    (void) n_hex; (void) g_hex; (void) rfc5054_compat;
    *bytes_B = (const unsigned char *) calloc(1, 1);
    *len_B = 0;
}

struct SRPVerifier *srp_verifier_new(SRP_HashAlgorithm alg, SRP_NGType ng_type, const char *username,
                                     const unsigned char *bytes_s, int len_s,
                                     const unsigned char *bytes_v, int len_v,
                                     const unsigned char *bytes_A, int len_A,
                                     const unsigned char *bytes_b, int len_b,
                                     const unsigned char **bytes_B, int *len_B,
                                     const char *n_hex, const char *g_hex, int rfc5054_compat) {
    (void) alg; (void) ng_type; (void) username; (void) bytes_s; (void) len_s; (void) bytes_v; (void) len_v;
    (void) bytes_A; (void) len_A; (void) bytes_b; (void) len_b; (void) n_hex; (void) g_hex; (void) rfc5054_compat;
    if (bytes_B) *bytes_B = NULL;
    if (len_B) *len_B = 0;
    return NULL;
}

void srp_verifier_delete(struct SRPVerifier *ver) { (void) ver; }
int srp_verifier_is_authenticated(struct SRPVerifier *ver) { (void) ver; return 0; }
const char *srp_verifier_get_username(struct SRPVerifier *ver) { (void) ver; return ""; }

const unsigned char *srp_verifier_get_session_key(struct SRPVerifier *ver, int *key_length) {
    (void) ver;
    if (key_length) *key_length = 0;
    return NULL;
}

int srp_verifier_get_session_key_length(struct SRPVerifier *ver) { (void) ver; return 0; }

void srp_verifier_verify_session(struct SRPVerifier *ver, const unsigned char *user_M, const unsigned char **bytes_HAMK) {
    (void) ver; (void) user_M;
    if (bytes_HAMK) *bytes_HAMK = NULL;
}
