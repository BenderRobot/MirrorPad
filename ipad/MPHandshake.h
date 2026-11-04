// Shared by the iPad receiver and the portable protocol regression tests.
#ifndef MP_HANDSHAKE_H
#define MP_HANDSHAKE_H
#include <stddef.h>

typedef enum { MPWaitHello, MPWaitOK, MPWaitPaired, MPAuthenticated } MPHandshakeState;

static int MPValidToken(const char *token, size_t length) {
 if(!token || length!=64)return 0;
 for(size_t i=0;i<length;i++)
  if(!((token[i]>='0'&&token[i]<='9')||(token[i]>='a'&&token[i]<='f')||(token[i]>='A'&&token[i]<='F')))return 0;
 return 1;
}

// Called only after HELLO validation and certificate pin verification (known PC),
// or after sending a pairing request (new PC).
static int MPHandshakeHello(MPHandshakeState *state, int knownPC) {
 if(*state!=MPWaitHello)return 0;
 *state=knownPC?MPWaitOK:MPWaitPaired;
 return 1;
}

static int MPHandshakeFinish(MPHandshakeState *state, int paired, size_t fields,
                             const char *token, size_t tokenLength) {
 if(paired){
  if(*state!=MPWaitPaired||fields!=2||!MPValidToken(token,tokenLength))return 0;
 } else if(*state!=MPWaitOK||fields!=1)return 0;
 *state=MPAuthenticated;
 return 1;
}
#endif
