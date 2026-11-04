#include <assert.h>
#include <stdio.h>
#include "../MPHandshake.h"

int main(void) {
 const char *token="0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
 // Every response shape in every state: only the two expected paths succeed.
 unsigned cases=0;
 for(int initial=MPWaitHello;initial<=MPAuthenticated;initial++)
  for(int paired=0;paired<=1;paired++)
   for(size_t fields=0;fields<=4;fields++){
    MPHandshakeState state=(MPHandshakeState)initial;
    int expected=paired?(initial==MPWaitPaired&&fields==2):(initial==MPWaitOK&&fields==1);
    assert(MPHandshakeFinish(&state,paired,fields,token,64)==expected);
    assert(state==(expected?MPAuthenticated:(MPHandshakeState)initial));
    cases++;
   }
 for(int known=0;known<=1;known++){
  MPHandshakeState state=MPWaitHello;
  assert(MPHandshakeHello(&state,known));
  assert(!MPHandshakeHello(&state,!known)); // repeated HELLO cannot switch paths
  assert(!MPHandshakeFinish(&state,known,known?2:1,token,64));
  assert(MPHandshakeFinish(&state,!known,known?1:2,token,64));
  assert(!MPHandshakeHello(&state,known));
  assert(!MPHandshakeFinish(&state,!known,known?1:2,token,64));
 }
 for(size_t n=0;n<64;n++)assert(!MPValidToken(token,n));
 assert(!MPValidToken(token,65));
 assert(!MPValidToken(NULL,64));
 for(size_t i=0;i<64;i++){
  char bad[64];
  for(size_t j=0;j<64;j++)bad[j]=token[j];
  bad[i]='g';
  MPHandshakeState state=MPWaitPaired;
  assert(!MPHandshakeFinish(&state,1,2,bad,64));
  assert(state==MPWaitPaired);
 }
 printf("PASS: %u response cases, both valid flows, replay and invalid tokens\n",cases);
 return 0;
}
