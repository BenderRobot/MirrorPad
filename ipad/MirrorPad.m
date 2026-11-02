// MirrorPad — reçoit un flux H.264 (conteneur FLV sur TCP) envoyé par le PC
// et l'affiche en plein écran. Décodage matériel via AVSampleBufferDisplayLayer (iOS 8+).
#import <UIKit/UIKit.h>
#import <AVFoundation/AVFoundation.h>
#import <CoreMedia/CoreMedia.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <arpa/inet.h>
#include <ifaddrs.h>
#include <unistd.h>
#include <errno.h>
#include <poll.h>
#include <fcntl.h>
#include <signal.h>
#import <Security/Security.h>
#import <Security/SecureTransport.h>
#import <CommonCrypto/CommonDigest.h>
#import <VideoToolbox/VideoToolbox.h>
#import <AudioToolbox/AudioToolbox.h>

#define MP_PORT 7272        // canal chiffré unique PC <-> iPad (image + tactile + contrôle)

static int MPListen(int port){
 int s=socket(AF_INET,SOCK_STREAM,0);if(s<0)return -1;
 int one=1;setsockopt(s,SOL_SOCKET,SO_REUSEADDR,&one,sizeof one);
 struct sockaddr_in a={.sin_len=sizeof a,.sin_family=AF_INET,.sin_port=htons(port),.sin_addr.s_addr=htonl(INADDR_ANY)};
 if(bind(s,(struct sockaddr*)&a,sizeof a)||listen(s,1)){close(s);return -1;}
 return s;
}

static NSString *MPLocalIP(void){
 struct ifaddrs *list=NULL;NSString *ip=nil;
 if(getifaddrs(&list)==0){
  for(struct ifaddrs *i=list;i;i=i->ifa_next){
   if(i->ifa_addr&&i->ifa_addr->sa_family==AF_INET&&!strcmp(i->ifa_name,"en0")){
    char b[INET_ADDRSTRLEN];inet_ntop(AF_INET,&((struct sockaddr_in*)i->ifa_addr)->sin_addr,b,sizeof b);ip=@(b);break;
   }
  }
  freeifaddrs(list);
 }
 return ip?:@"Wi-Fi non connecté";
}
static BOOL MPReadFully(int fd,void *buf,size_t n){
 uint8_t *p=buf;
 // read() et non recv() : le flux vidéo arrive par un tube local, pas par une connexion réseau
 while(n){ssize_t r=read(fd,p,n);if(r<=0){if(r<0&&errno==EINTR)continue;return NO;}p+=r;n-=(size_t)r;}
 return YES;
}
static uint32_t MPBE24(const uint8_t *p){return (uint32_t)p[0]<<16|(uint32_t)p[1]<<8|p[2];}
static uint32_t MPBE32(const uint8_t *p){return (uint32_t)p[0]<<24|MPBE24(p+1);}

#pragma mark - Réception réseau

@interface MPReceiver : NSObject
@property(nonatomic,copy) void (^onState)(BOOL connected);
@property(nonatomic,copy) void (^onSample)(CMSampleBufferRef sample);
@property(nonatomic,copy) void (^onSize)(CGSize size);
- (void)runOnFd:(int)fd;
- (void)takeBytes:(uint64_t*)bytes frames:(uint64_t*)frames; // compteurs depuis le dernier appel
@end
@implementation MPReceiver {
 CMVideoFormatDescriptionRef _format;
 uint8_t *_buf;size_t _cap;
 volatile uint64_t _bytes,_frames;
}
- (void)takeBytes:(uint64_t*)bytes frames:(uint64_t*)frames {*bytes=_bytes;*frames=_frames;_bytes=0;_frames=0;}
// Décode un flux FLV lu sur fd (extrémité de lecture d'un tube) dans un fil dédié
- (void)runOnFd:(int)fd {
 [NSThread detachNewThreadWithBlock:^{
  if(self.onState)self.onState(YES);
  [self readFLV:fd];
  close(fd);
  if(self->_format){CFRelease(self->_format);self->_format=NULL;}
  if(self.onState)self.onState(NO);
 }];
}
// FLV : en-tête 9 octets, puis [PreviousTagSize 4][en-tête de tag 11][données]...
// Tag vidéo H.264 : [type/codec][AVCPacketType][CompositionTime 3][charge utile]
//   AVCPacketType 0 = avcC (SPS/PPS), 1 = NAL préfixés par leur longueur (format AVCC, utilisable tel quel)
- (void)readFLV:(int)fd {
 uint8_t h[11];
 if(!MPReadFully(fd,h,9)||memcmp(h,"FLV",3))return;
 uint32_t off=MPBE32(h+5);if(off<9||off>1024)return;
 for(uint32_t skip=off-9;skip;skip--)if(!MPReadFully(fd,h,1))return;
 for(;;){
  if(!MPReadFully(fd,h,4))return;
  if(!MPReadFully(fd,h,11))return;
  uint32_t size=MPBE24(h+1);
  if(size>(16u<<20))return;
  if(size>_cap){uint8_t *n=realloc(_buf,size);if(!n)return;_buf=n;_cap=size;}
  if(size&&!MPReadFully(fd,_buf,size))return;
  _bytes+=size+15;
  if((h[0]&0x1f)!=9||size<6||(_buf[0]&0x0f)!=7)continue;
  if(_buf[1]==0)[self setAvcC:_buf+5 length:size-5];
  else if(_buf[1]==1&&_format)[self emit:_buf+5 length:size-5];
 }
}
- (void)setAvcC:(const uint8_t*)p length:(size_t)n {
 if(n<7)return;
 int lenSize=(p[4]&3)+1;
 const uint8_t *ps[16];size_t sz[16];size_t count=0,i=6;
 unsigned nsps=p[5]&0x1f;
 for(unsigned k=0;k<nsps;k++){if(i+2>n)return;size_t l=(size_t)p[i]<<8|p[i+1];i+=2;if(i+l>n||count>=16)return;ps[count]=p+i;sz[count++]=l;i+=l;}
 if(i>=n)return;
 unsigned npps=p[i++];
 for(unsigned k=0;k<npps;k++){if(i+2>n)return;size_t l=(size_t)p[i]<<8|p[i+1];i+=2;if(i+l>n||count>=16)return;ps[count]=p+i;sz[count++]=l;i+=l;}
 if(count<2)return;
 CMVideoFormatDescriptionRef f=NULL;
 OSStatus st=CMVideoFormatDescriptionCreateFromH264ParameterSets(kCFAllocatorDefault,count,ps,sz,lenSize,&f);
 if(st!=noErr){NSLog(@"MirrorPad: SPS/PPS refusés (%d)",(int)st);return;}
 if(_format)CFRelease(_format);
 _format=f;
 CMVideoDimensions d=CMVideoFormatDescriptionGetDimensions(f);
 NSLog(@"MirrorPad: flux %dx%d",d.width,d.height);
 if(self.onSize)self.onSize(CGSizeMake(d.width,d.height));
}
- (void)emit:(const uint8_t*)p length:(size_t)n {
 void *mem=malloc(n);if(!mem)return;memcpy(mem,p,n);
 CMBlockBufferRef bb=NULL;
 if(CMBlockBufferCreateWithMemoryBlock(kCFAllocatorDefault,mem,n,kCFAllocatorMalloc,NULL,0,n,0,&bb)!=kCMBlockBufferNoErr){free(mem);return;}
 CMSampleBufferRef sb=NULL;size_t sz=n;
 OSStatus st=CMSampleBufferCreateReady(kCFAllocatorDefault,bb,_format,1,0,NULL,1,&sz,&sb);
 CFRelease(bb);
 if(st!=noErr||!sb)return;
 CFArrayRef att=CMSampleBufferGetSampleAttachmentsArray(sb,YES);
 if(att&&CFArrayGetCount(att)>0)
  CFDictionarySetValue((CFMutableDictionaryRef)CFArrayGetValueAtIndex(att,0),kCMSampleAttachmentKey_DisplayImmediately,kCFBooleanTrue);
 _frames++;
 if(self.onSample)self.onSample(sb);
 CFRelease(sb);
}
@end

#pragma mark - Tactile

// Session chiffrée avec le PC (port 7272). Le PC se connecte et joue le rôle de serveur TLS :
// l'iPad (client TLS) vérifie l'empreinte SHA-256 du certificat du PC mémorisée à l'appairage,
// puis s'authentifie avec le jeton reçu à l'appairage.
// Trames : [type 1 octet][longueur 3 octets][données]  1 = ligne de contrôle, 2 = vidéo FLV
//   PC -> HELLO 2 <idPC> <nomPC> ; iPad -> AUTH <idIPad> <jeton> | PAIR <idIPad> <nomIPad> <nonce>
//   PC -> OK | PAIRED <jeton> | ERR <raison> ; ensuite : "V NEW" / "V END" encadrent chaque flux vidéo.
//   Lignes de contrôle : iPad -> PC gestes ("D x y" "M" "U" "R" "W n" "Z n" "T texte" "K BACK"), "O P|L", "P n", "S fps mbps"
//                        PC -> iPad "P n", "N nom", "C fps mbps encodeur"
static NSString *MPHex(const uint8_t *b,size_t n){
 NSMutableString *h=[NSMutableString stringWithCapacity:n*2];
 for(size_t i=0;i<n;i++)[h appendFormat:@"%02x",b[i]];
 return h;
}
static NSString *MPEsc(NSString *t){
 return [t stringByAddingPercentEncodingWithAllowedCharacters:[NSCharacterSet characterSetWithCharactersInString:@"abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.~"]];
}
static OSStatus MPSSLRead(SSLConnectionRef c,void *data,size_t *len){
 int fd=(int)(intptr_t)c;size_t want=*len,got=0;
 while(got<want){
  ssize_t r=recv(fd,(char*)data+got,want-got,0);
  if(r>0){got+=(size_t)r;continue;}
  if(r==0){*len=got;return errSSLClosedGraceful;}
  if(errno==EINTR)continue;
  *len=got;
  return (errno==EAGAIN||errno==EWOULDBLOCK)?errSSLWouldBlock:errSSLClosedAbort;
 }
 *len=got;return noErr;
}
static OSStatus MPSSLWrite(SSLConnectionRef c,const void *data,size_t *len){
 int fd=(int)(intptr_t)c;size_t want=*len,done=0;
 while(done<want){
  ssize_t r=send(fd,(const char*)data+done,want-done,0);
  if(r>0){done+=(size_t)r;continue;}
  if(r<0&&errno==EINTR)continue;
  if(r<0&&(errno==EAGAIN||errno==EWOULDBLOCK)){struct pollfd p={fd,POLLOUT,0};if(poll(&p,1,5000)>0)continue;}
  *len=done;return errSSLClosedAbort;
 }
 *len=done;return noErr;
}
// PC appairés : { idPC : { name, fp, token, date } }
static NSDictionary *MPPairedPCs(void){return [NSUserDefaults.standardUserDefaults dictionaryForKey:@"pairedPCs"]?:@{};}
static void MPSetPairedPC(NSString *pcId,NSDictionary *v){
 NSMutableDictionary *d=[MPPairedPCs() mutableCopy];
 if(v)d[pcId]=v;else [d removeObjectForKey:pcId];
 [NSUserDefaults.standardUserDefaults setObject:d forKey:@"pairedPCs"];
}
static NSString *MPDeviceId(void){
 NSUserDefaults *d=NSUserDefaults.standardUserDefaults;
 NSString *i=[d stringForKey:@"deviceId"];
 if(!i){i=[[NSUUID UUID].UUIDString stringByReplacingOccurrencesOfString:@"-" withString:@""].lowercaseString;[d setObject:i forKey:@"deviceId"];}
 return i;
}

@interface MPSecureServer : NSObject
@property(nonatomic,copy) void (^onLine)(NSString *line);                      // fil principal, après authentification
@property(nonatomic,copy) void (^onAuthenticated)(NSString *pcName);           // fil principal
@property(nonatomic,copy) void (^onClosed)(void);                              // fil principal
@property(nonatomic,copy) void (^onPairing)(NSString *pcName,NSString *code);  // fil principal : afficher le code
@property(nonatomic,copy) void (^onNotice)(NSString *title,NSString *text,BOOL ok); // fil principal : résultat / alerte
@property(nonatomic,copy) void (^onVideoStart)(int fd);                        // fil réseau : nouveau flux à décoder
@property(nonatomic,copy) void (^onBinary)(uint8_t type,NSData *data);          // fil principal : image copiée sur le PC…
@property(nonatomic,readonly) BOOL connected;
@property(atomic,readonly,copy) NSString *peer;
@property(atomic) BOOL paused;   // app en arrière-plan : on refuse les connexions
- (void)start;
- (void)send:(NSString*)line;
- (void)disconnect;               // envoie la file d'attente puis ferme la session
- (void)sendBinary:(uint8_t)type data:(NSData*)d;   // 3 webcam, 4 image copiée, 5 fichier, 6 micro, 8 fichier en morceaux
- (NSUInteger)pending;                               // trames en attente d'envoi (pour ne pas saturer la mémoire)
@property(nonatomic,copy) void (^onSound)(NSData *pcm);   // 7 : son du PC (fil réseau)
@end
@implementation MPSecureServer {
 volatile BOOL _closing;
 SSLContextRef _ctx;
 int _wake[2];
 NSMutableArray<NSData*> *_queue;
 volatile BOOL _auth;
 int _videoFd;
}
- (instancetype)init {
 if((self=[super init])){_queue=[NSMutableArray array];pipe(_wake);fcntl(_wake[0],F_SETFL,O_NONBLOCK);fcntl(_wake[1],F_SETFL,O_NONBLOCK);_videoFd=-1;}
 return self;
}
- (BOOL)connected {return _auth;}
- (void)start {[NSThread detachNewThreadWithBlock:^{[self serve];}];}
- (void)ui:(dispatch_block_t)b {dispatch_async(dispatch_get_main_queue(),b);}
- (void)serve {
 int srv=-1;
 for(;;){
  if(srv<0){srv=MPListen(MP_PORT);if(srv<0){sleep(1);continue;}}
  struct sockaddr_in from;socklen_t len=sizeof from;
  int c=accept(srv,(struct sockaddr*)&from,&len);
  if(c<0){if(errno!=EINTR){close(srv);srv=-1;sleep(1);}continue;}
  if(self.paused){close(c);continue;}
  char ip[INET_ADDRSTRLEN]="";inet_ntop(AF_INET,&from.sin_addr,ip,sizeof ip);
  _peer=@(ip);
  [self runSession:c];   // une session à la fois ; la suivante attend la fin de celle-ci
 }
}
- (void)send:(NSString*)line {
 if(!_auth)return;
 if([line hasSuffix:@"\n"])line=[line substringToIndex:line.length-1];
 [self enqueue:1 data:[line dataUsingEncoding:NSUTF8StringEncoding]];
}
- (void)sendBinary:(uint8_t)type data:(NSData*)d {
 if(!_auth||d.length>=(1u<<24))return;
 if(type==3||type==6){   // flux temps réel : si la file déborde (Wi-Fi lent), on jette plutôt que d'accumuler du retard
  @synchronized(_queue){if(_queue.count>60)return;}
 }
 [self enqueue:type data:d];
}
- (NSUInteger)pending {@synchronized(_queue){return _queue.count;}}
- (void)disconnect {
 _closing=YES;
 uint8_t one=1;write(_wake[1],&one,1);
}
- (void)enqueue:(uint8_t)type data:(NSData*)d {
 NSMutableData *f=[NSMutableData dataWithLength:4];
 uint8_t *h=f.mutableBytes;h[0]=type;h[1]=(uint8_t)(d.length>>16);h[2]=(uint8_t)(d.length>>8);h[3]=(uint8_t)d.length;
 [f appendData:d];
 @synchronized(_queue){[_queue addObject:f];}
 uint8_t one=1;write(_wake[1],&one,1);
}
- (BOOL)write:(NSData*)d {
 size_t done=0;
 while(done<d.length){
  size_t n=0;OSStatus st=SSLWrite(_ctx,(const uint8_t*)d.bytes+done,d.length-done,&n);
  done+=n;
  if(st!=noErr&&st!=errSSLWouldBlock)return NO;
 }
 return YES;
}
- (BOOL)sendNow:(NSString*)line {
 NSData *d=[line dataUsingEncoding:NSUTF8StringEncoding];
 NSMutableData *f=[NSMutableData dataWithLength:4];
 uint8_t *h=f.mutableBytes;h[0]=1;h[1]=(uint8_t)(d.length>>16);h[2]=(uint8_t)(d.length>>8);h[3]=(uint8_t)d.length;
 [f appendData:d];
 return [self write:f];
}
- (void)endVideo {if(_videoFd>=0){close(_videoFd);_videoFd=-1;}}
- (void)runSession:(int)fd {
 int one=1;
 setsockopt(fd,IPPROTO_TCP,TCP_NODELAY,&one,sizeof one);
 setsockopt(fd,SOL_SOCKET,SO_NOSIGPIPE,&one,sizeof one);
 fcntl(fd,F_SETFL,fcntl(fd,F_GETFL)|O_NONBLOCK);
 _ctx=SSLCreateContext(NULL,kSSLClientSide,kSSLStreamType);
 SSLSetIOFuncs(_ctx,MPSSLRead,MPSSLWrite);
 SSLSetConnection(_ctx,(SSLConnectionRef)(intptr_t)fd);
 SSLSetProtocolVersionMin(_ctx,kTLSProtocol12);
 SSLSetSessionOption(_ctx,kSSLSessionOptionBreakOnServerAuth,true);   // on vérifie nous-mêmes le certificat (épinglage)
 @synchronized(_queue){[_queue removeAllObjects];}
 _auth=NO;_closing=NO;
 NSData *certDer=nil;NSString *fp=nil;
 NSString *pcId=nil,*pcName=nil;BOOL pairing=NO;
 NSMutableData *in=[NSMutableData data];
 CFAbsoluteTime last=CFAbsoluteTimeGetCurrent(),start=last;

 // 1. poignée de main TLS
 for(;;){
  OSStatus st=SSLHandshake(_ctx);
  if(st==noErr)break;
  if(st==errSSLPeerAuthCompleted){
   SecTrustRef trust=NULL;SSLCopyPeerTrust(_ctx,&trust);
   if(trust&&SecTrustGetCertificateCount(trust)>0){
    CFDataRef der=SecCertificateCopyData(SecTrustGetCertificateAtIndex(trust,0));
    certDer=(__bridge_transfer NSData*)der;
    uint8_t h[CC_SHA256_DIGEST_LENGTH];CC_SHA256(certDer.bytes,(CC_LONG)certDer.length,h);
    fp=MPHex(h,sizeof h);
   }
   if(trust)CFRelease(trust);
   continue;
  }
  if(st==errSSLWouldBlock){
   struct pollfd p={fd,POLLIN,0};
   if(CFAbsoluteTimeGetCurrent()-start>10||poll(&p,1,1000)<0)goto done;
   continue;
  }
  NSLog(@"MirrorPad: échec TLS %d",(int)st);goto done;
 }
 if(!fp)goto done;

 // 2. boucle : lecture des trames, envoi de la file d'attente
 for(;;){
  size_t buffered=0;SSLGetBufferedReadSize(_ctx,&buffered);
  if(!buffered){
   struct pollfd p[2]={{fd,POLLIN,0},{_wake[0],POLLIN,0}};
   if(poll(p,2,1000)<0&&errno!=EINTR)break;
   if(p[1].revents){uint8_t tmp[64];while(read(_wake[0],tmp,sizeof tmp)>0){}}
  }
  // lecture
  // au plus 256 Ko lus par tour : sinon, quand l'écran du PC arrive en continu, la file d'envoi
  // (caméra, micro…) n'est jamais vidée et l'image de la caméra se fige côté PC
  uint8_t buf[32768];size_t readNow=0;
  for(;;){
   if(readNow>=262144)break;
   size_t n=0;OSStatus st=SSLRead(_ctx,buf,sizeof buf,&n);
   readNow+=n;
   if(n){[in appendBytes:buf length:n];last=CFAbsoluteTimeGetCurrent();}
   if(st==errSSLWouldBlock)break;
   if(st!=noErr)goto done;
   if(!n)break;
  }
  // traitement des trames complètes
  while(in.length>=4){
   const uint8_t *h=in.bytes;size_t len=(size_t)h[1]<<16|(size_t)h[2]<<8|h[3];
   if(in.length<4+len)break;
   uint8_t type=h[0];
   if(type==2){
    if(_auth&&_videoFd>=0){
     size_t w=0;while(w<len){ssize_t r=write(_videoFd,h+4+w,len-w);if(r<=0){if(r<0&&errno==EINTR)continue;[self endVideo];break;}w+=(size_t)r;}
    }
   } else if(type==4&&_auth){
    NSData *img=[NSData dataWithBytes:h+4 length:len];
    [self ui:^{if(self.onBinary)self.onBinary(4,img);}];
   } else if(type==8&&_auth){
    NSData *chunk=[NSData dataWithBytes:h+4 length:len];
    [self ui:^{if(self.onBinary)self.onBinary(8,chunk);}];
   } else if(type==7&&_auth){
    // son du PC : remis directement au haut-parleur (sans passer par le fil principal)
    if(self.onSound)self.onSound([NSData dataWithBytes:h+4 length:len]);
   } else if(type==1){
    NSString *line=[[NSString alloc] initWithBytes:h+4 length:len encoding:NSUTF8StringEncoding]?:@"";
    NSArray<NSString*> *p=[line componentsSeparatedByString:@" "];
    NSString *k=p.firstObject;
    if(!_auth){
     if([k isEqualToString:@"HELLO"]&&p.count>=4){
      pcId=p[2];pcName=[p[3] stringByRemovingPercentEncoding]?:p[3];
      NSDictionary *known=MPPairedPCs()[pcId];
      if(known){
       if(![known[@"fp"] isEqualToString:fp]){
        NSString *n=pcName;
        [self sendNow:@"ERR CERT"];
        [self ui:^{if(self.onNotice)self.onNotice(@"Connexion refusée",[NSString stringWithFormat:@"Le certificat de « %@ » a changé. Si ce PC a été réinstallé, oubliez-le dans Réglages puis appairez-le à nouveau.",n],NO);}];
        goto done;
       }
       [self sendNow:[NSString stringWithFormat:@"AUTH %@ %@",MPDeviceId(),known[@"token"]]];
      } else {
       uint8_t nonce[16];SecRandomCopyBytes(kSecRandomDefault,sizeof nonce,nonce);
       NSMutableData *m=[certDer mutableCopy];[m appendBytes:nonce length:sizeof nonce];
       uint8_t d[CC_SHA256_DIGEST_LENGTH];CC_SHA256(m.bytes,(CC_LONG)m.length,d);
       uint32_t v=((uint32_t)d[0]<<24|(uint32_t)d[1]<<16|(uint32_t)d[2]<<8|d[3])%1000000;
       NSString *code=[NSString stringWithFormat:@"%06u",v],*n=pcName;
       pairing=YES;
       [self ui:^{if(self.onPairing)self.onPairing(n,code);}];
       [self sendNow:[NSString stringWithFormat:@"PAIR %@ %@ %@",MPDeviceId(),MPEsc(UIDevice.currentDevice.name),MPHex(nonce,sizeof nonce)]];
      }
     } else if([k isEqualToString:@"OK"]||([k isEqualToString:@"PAIRED"]&&p.count>=2&&pairing)){
      if(pairing)MPSetPairedPC(pcId,@{@"name":pcName?:@"PC",@"fp":fp,@"token":p[1],@"date":[NSDate date]});
      _auth=YES;
      NSString *n=pcName;BOOL wasPairing=pairing;
      [self ui:^{
       if(wasPairing&&self.onNotice)self.onNotice(@"Appairage réussi",[NSString stringWithFormat:@"« %@ » est maintenant autorisé à utiliser cet iPad.",n],YES);
       if(self.onAuthenticated)self.onAuthenticated(n);
      }];
     } else if([k isEqualToString:@"ERR"]){
      NSString *why=p.count>1?p[1]:@"",*n=pcName;
      if([why isEqualToString:@"AUTH"]&&pcId)MPSetPairedPC(pcId,nil);   // le PC nous a oubliés : nouvel appairage au prochain essai
      [self ui:^{if(self.onNotice)self.onNotice(@"Connexion refusée",[why isEqualToString:@"REFUSED"]?[NSString stringWithFormat:@"L'appairage a été refusé sur « %@ ».",n]:@"Le PC ne reconnaît plus cet iPad : un nouvel appairage va être proposé.",NO);}];
      goto done;
     }
    } else if([k isEqualToString:@"V"]&&p.count>=2){
     [self endVideo];
     if([p[1] isEqualToString:@"NEW"]){
      int pf[2];
      if(pipe(pf)==0){
       fcntl(pf[1],F_SETNOSIGPIPE,1);
       _videoFd=pf[1];
       if(self.onVideoStart)self.onVideoStart(pf[0]);else close(pf[0]);
      }
     }
    } else {
     [self ui:^{if(self.onLine)self.onLine(line);}];
    }
   }
   [in replaceBytesInRange:NSMakeRange(0,4+len) withBytes:NULL length:0];
  }
  // écriture
  NSArray<NSData*> *out;
  @synchronized(_queue){out=[_queue copy];[_queue removeAllObjects];}
  for(NSData *f in out)if(![self write:f])goto done;
  if(_closing)break;
  // le PC envoie au moins un écho de latence par seconde : 20 s de silence = connexion perdue
  if(CFAbsoluteTimeGetCurrent()-last>20)break;
 }
done:
 [self endVideo];
 BOOL wasAuth=_auth;_auth=NO;
 SSLClose(_ctx);CFRelease(_ctx);_ctx=NULL;
 close(fd);
 [self ui:^{if(wasAuth&&self.onClosed)self.onClosed();}];
}
@end

// Gestes : 1 doigt tapé = clic · 1 doigt glissé = glisser · appui long = clic droit
//          2 doigts tapés = clic droit · 2 doigts glissés = molette (avec élan) · pincer = zoom (Ctrl + molette)
//          3 doigts tapés = afficher / masquer le clavier (le texte tapé est envoyé au PC)
// Lignes envoyées au PC : "D x y" "M x y" "U x y" "R x y" "W n" "Z n" "T texte%encodé" "K BACK"
@interface MPTouchView : UIView<UIKeyInput>
@property(nonatomic,copy) void (^emit)(NSString *line);
@property(nonatomic) CGSize videoSize;
@property(nonatomic) BOOL longPressEnabled,inertiaEnabled;
@property(nonatomic,copy) void (^onFourFingerTap)(void);
@property(nonatomic,copy) void (^onThreeFingerSwipeUp)(void);
@property(nonatomic,copy) void (^onTwoFingerHold)(CGPoint p);   // deux doigts immobiles 0,6 s : loupe   // envoie la fenêtre active du PC sur l'iPad (ou la ramène)
@property(nonatomic) NSInteger inputMode;   // 0 souris, 1 tactile Windows natif, 2 stylet
@property(nonatomic) BOOL penEraser;
@end
@implementation MPTouchView {
 NSMutableDictionary<NSValue*,NSNumber*> *_ids;   // doigt -> numéro de contact (tactile natif)
 BOOL _quad,_quadMoved;     // tactile natif : 3 ou 4 doigts posés = geste réservé à l'iPad (clavier, fenêtre, réglages)
 NSUInteger _quadFingers;
 CGPoint _holdStart;
 int _mode;                 // 0 rien, 1 un doigt, 2 deux doigts, 3 trois doigts
 BOOL _dragging,_moved,_longFired,_decided,_pinching;
 __weak UITouch *_primary;
 CGPoint _start,_last,_centroid,_centroidStart;
 CGFloat _scrollRef,_scrollAcc,_pinchRef,_velocity;
 NSTimeInterval _lastMoveTime;
 CADisplayLink *_inertia;
}
- (instancetype)initWithFrame:(CGRect)f {
 if((self=[super initWithFrame:f])){self.multipleTouchEnabled=YES;self.backgroundColor=UIColor.clearColor;_longPressEnabled=YES;_inertiaEnabled=YES;}
 return self;
}
- (void)out:(NSString*)line {if(self.emit)self.emit(line);}
- (CGPoint)norm:(CGPoint)p {
 CGRect r=self.bounds;
 if(_videoSize.width>0&&_videoSize.height>0)r=AVMakeRectWithAspectRatioInsideRect(_videoSize,self.bounds);
 CGFloat x=(p.x-r.origin.x)/r.size.width,y=(p.y-r.origin.y)/r.size.height;
 return CGPointMake(fmin(fmax(x,0),1),fmin(fmax(y,0),1));
}
- (void)send:(char)c at:(CGPoint)p {
 CGPoint n=[self norm:p];
 [self out:[NSString stringWithFormat:@"%c %.5f %.5f\n",c,n.x,n.y]];
}
// Petit cercle lumineux qui confirme un geste (clic droit, clavier)
- (void)ripple:(CGPoint)p color:(UIColor*)color {
 CAShapeLayer *r=[CAShapeLayer layer];
 r.path=[UIBezierPath bezierPathWithOvalInRect:CGRectMake(-30,-30,60,60)].CGPath;
 r.position=p;r.fillColor=[color colorWithAlphaComponent:.25].CGColor;r.strokeColor=color.CGColor;r.lineWidth=2;
 [self.layer addSublayer:r];
 CABasicAnimation *g=[CABasicAnimation animationWithKeyPath:@"transform.scale"];g.fromValue=@.3;g.toValue=@1.2;
 CABasicAnimation *f=[CABasicAnimation animationWithKeyPath:@"opacity"];f.fromValue=@1;f.toValue=@0;
 CAAnimationGroup *grp=[CAAnimationGroup animation];grp.animations=@[g,f];grp.duration=.45;
 grp.removedOnCompletion=NO;grp.fillMode=kCAFillModeForwards;
 [CATransaction begin];[CATransaction setCompletionBlock:^{[r removeFromSuperlayer];}];
 [r addAnimation:grp forKey:nil];[CATransaction commit];
}
- (NSArray<UITouch*>*)active:(UIEvent*)e {
 NSMutableArray *a=[NSMutableArray array];
 for(UITouch *t in [e touchesForView:self])
  if(t.phase!=UITouchPhaseEnded&&t.phase!=UITouchPhaseCancelled)[a addObject:t];
 return a;
}
- (CGPoint)centroid:(NSArray<UITouch*>*)ts {
 CGFloat x=0,y=0;for(UITouch *t in ts){CGPoint p=[t locationInView:self];x+=p.x;y+=p.y;}
 return CGPointMake(x/ts.count,y/ts.count);
}
- (CGFloat)spread:(NSArray<UITouch*>*)ts {
 if(ts.count<2)return 0;
 CGPoint a=[ts[0] locationInView:self],b=[ts[1] locationInView:self];
 return hypot(a.x-b.x,a.y-b.y);
}
// Tactile natif : annule côté PC tous les contacts en cours (avant un geste réservé à l'iPad)
- (void)cancelNative:(UIEvent*)e {
 NSMutableString *f=[NSMutableString stringWithString:@"F"];
 for(UITouch *t in [e touchesForView:self]){
  NSNumber *n=_ids[[NSValue valueWithNonretainedObject:t]];
  if(!n)continue;
  CGPoint q=[self norm:[t locationInView:self]];
  [f appendFormat:@" %@:%.5f:%.5f:c",n,q.x,q.y];
 }
 [_ids removeAllObjects];
 if(f.length>1)[self out:[f stringByAppendingString:@"\n"]];
}
// Tactile Windows natif : tous les doigts posés, à chaque évènement (« F id:x:y:état … »)
- (void)touchFrame:(UIEvent*)e {
 if(!_ids)_ids=[NSMutableDictionary dictionary];
 NSMutableString *f=[NSMutableString stringWithString:@"F"];
 NSMutableArray *gone=[NSMutableArray array];
 for(UITouch *t in [e touchesForView:self]){
  NSValue *k=[NSValue valueWithNonretainedObject:t];
  NSNumber *n=_ids[k];
  if(!n){
   NSInteger i=0;while([_ids.allValues containsObject:@(i)])i++;
   if(i>9)continue;
   n=@(i);_ids[k]=n;
  }
  char st='m';
  if(t.phase==UITouchPhaseBegan)st='d';
  else if(t.phase==UITouchPhaseEnded){st='u';[gone addObject:k];}
  else if(t.phase==UITouchPhaseCancelled){st='c';[gone addObject:k];}
  CGPoint q=[self norm:[t locationInView:self]];
  [f appendFormat:@" %@:%.5f:%.5f:%c",n,q.x,q.y,st];
 }
 [_ids removeObjectsForKeys:gone];
 if(f.length>1)[self out:[f stringByAppendingString:@"\n"]];
}
// Stylet : pression approchée par la surface de contact (pas d'Apple Pencil sur cet iPad)
- (void)pen:(NSString*)kind touch:(UITouch*)t at:(CGPoint)p {
 CGPoint n=[self norm:p];
 CGFloat pr=t?fmin(1,fmax(.15,(t.majorRadius-4)/18)):0;
 [self out:[NSString stringWithFormat:@"%@ %.5f %.5f %.3f%@\n",kind,n.x,n.y,pr,_penEraser?@" e":@""]];
}
- (void)cancelHold {[NSObject cancelPreviousPerformRequestsWithTarget:self selector:@selector(twoFingerHold) object:nil];}
- (void)twoFingerHold {
 if(_inputMode==1){
  if(_quad)return;
  _quad=YES;_quadFingers=2;_quadMoved=YES;   // les doigts restent posés : rien d'autre ne se déclenchera au lever
 } else {
  if(_mode!=2||_decided)return;
  _decided=YES;_moved=YES;                    // pas de clic droit au lever
 }
 if(self.onTwoFingerHold)self.onTwoFingerHold(_holdStart);
}
- (void)cancelLongPress {[NSObject cancelPreviousPerformRequestsWithTarget:self selector:@selector(longPress) object:nil];}
- (void)longPress {
 if(_mode!=1||_dragging)return;
 _longFired=YES;
 [self send:'R' at:_start];
 [self ripple:_start color:[UIColor colorWithRed:.55 green:.75 blue:1 alpha:1]];
}
// Molette : 12 points de déplacement = 40 unités Windows (120 = un cran)
static const CGFloat MPScrollGain=2.4;
- (void)scrollBy:(CGFloat)dy {
 // défilement fin : le contenu suit le doigt (≈ 2,4 unités Windows par point ; 120 = un cran ≈ 100 px)
 _scrollAcc+=dy;
 int units=(int)(_scrollAcc*MPScrollGain);
 if(units){_scrollAcc-=units/MPScrollGain;[self out:[NSString stringWithFormat:@"W %d\n",units]];}
}
- (void)stopInertia {[_inertia invalidate];_inertia=nil;}
- (void)inertiaTick:(CADisplayLink*)link {
 CGFloat dt=link.duration>0?link.duration:1.0/60;
 [self scrollBy:_velocity*dt];
 _velocity*=pow(.94,dt*60);
 if(fabs(_velocity)<40)[self stopInertia];
}

- (void)touchesBegan:(NSSet<UITouch*>*)touches withEvent:(UIEvent*)e {
 (void)touches;
 [self stopInertia];
 if(_inputMode==1){
  NSArray *all=[self active:e];
  if(all.count==2&&!_quad){_holdStart=[self centroid:all];[self cancelHold];[self performSelector:@selector(twoFingerHold) withObject:nil afterDelay:.6];}
  else [self cancelHold];
  if(all.count>=3&&!_quad){[self cancelNative:e];_quad=YES;_quadMoved=NO;_quadFingers=0;_centroidStart=_centroid=[self centroid:all];}
  if(_quad)_quadFingers=MAX(_quadFingers,all.count);
  if(!_quad)[self touchFrame:e];
  return;
 }
 NSArray *a=[self active:e];
 if(a.count>=3){
  [self cancelLongPress];
  if(_mode==1&&_dragging)[self send:'U' at:_last];
  if(_mode!=3&&_mode!=4)_moved=NO;
  _mode=a.count>=4?4:3;_centroidStart=[self centroid:a];_dragging=NO;
 } else if(_mode==0&&a.count==1){
  _mode=1;_dragging=NO;_longFired=NO;_primary=a[0];_start=_last=[a[0] locationInView:self];
  if(_inputMode==2){_dragging=YES;[self pen:@"PD" touch:a[0] at:_start];}   // stylet : le trait commence tout de suite
  else if(_longPressEnabled)[self performSelector:@selector(longPress) withObject:nil afterDelay:.55];
 } else if(a.count==2&&_mode!=3&&!(_mode==1&&(_dragging||_longFired))){
  [self cancelLongPress];
  _mode=2;_moved=NO;_decided=NO;_pinching=NO;_velocity=0;
  _centroid=_centroidStart=[self centroid:a];_scrollRef=_centroid.y;_scrollAcc=0;
  _pinchRef=[self spread:a];_lastMoveTime=e.timestamp;
  _holdStart=_centroid;[self cancelHold];[self performSelector:@selector(twoFingerHold) withObject:nil afterDelay:.6];
 }
}
- (void)touchesMoved:(NSSet<UITouch*>*)touches withEvent:(UIEvent*)e {
 (void)touches;
 if(_inputMode==1){
  if(_quad){
   NSArray *all=[self active:e];
   if(all.count>=3){CGPoint c=[self centroid:all];_centroid=c;if(hypot(c.x-_centroidStart.x,c.y-_centroidStart.y)>40)_quadMoved=YES;}
   return;
  }
  {NSArray *all=[self active:e];if(all.count!=2||hypot([self centroid:all].x-_holdStart.x,[self centroid:all].y-_holdStart.y)>12)[self cancelHold];}
  [self touchFrame:e];return;
 }
 NSArray *a=[self active:e];
 if(_mode==1&&_primary&&_inputMode==2){
  if(_dragging){_last=[_primary locationInView:self];[self pen:@"PM" touch:_primary at:_last];}
 } else if(_mode==1&&_primary){
  CGPoint p=[_primary locationInView:self];
  if(!_dragging&&!_longFired&&hypot(p.x-_start.x,p.y-_start.y)>8){[self cancelLongPress];_dragging=YES;[self send:'D' at:_start];}
  if(_dragging)[self send:'M' at:p];
  _last=p;
 } else if(_mode==2&&a.count>=2){
  CGPoint c=[self centroid:a];CGFloat d=[self spread:a];
  if(!_decided){
   CGFloat pinch=fabs(d-_pinchRef),pan=hypot(c.x-_centroidStart.x,c.y-_centroidStart.y);
   if(pinch>24&&pinch>pan*1.2){_decided=YES;_pinching=YES;_moved=YES;_pinchRef=d;}
   else if(pan>10){_decided=YES;_moved=YES;}
   if(_decided)[self cancelHold];
  }
  if(_decided&&_pinching){
   // un cran de zoom tous les 12 % d'écartement
   if(d>_pinchRef*1.12){_pinchRef=d;[self out:@"Z 1\n"];}
   else if(d<_pinchRef/1.12){_pinchRef=d;[self out:@"Z -1\n"];}
  } else if(_decided){
   CGFloat dy=c.y-_scrollRef;_scrollRef=c.y;
   NSTimeInterval dt=e.timestamp-_lastMoveTime;_lastMoveTime=e.timestamp;
   if(dt>0)_velocity=.7*_velocity+.3*(dy/dt);
   [self scrollBy:dy];
  }
  _centroid=c;
 } else if((_mode==3||_mode==4)&&a.count>=3){
  CGPoint c=[self centroid:a];
  if(hypot(c.x-_centroidStart.x,c.y-_centroidStart.y)>20)_moved=YES;
  _centroid=c;
 }
}
- (void)finish:(UIEvent*)e cancelled:(BOOL)cancelled {
 if([self active:e].count<2)[self cancelHold];
 if(_inputMode==1){
  if(_quad){
   // 3 / 4 doigts : on attend qu'ils soient tous levés, puis on décide du geste
   if([self active:e].count==0){
    _quad=NO;
    if(cancelled)return;
    CGFloat up=_centroidStart.y-_centroid.y,side=fabs(_centroid.x-_centroidStart.x);
    if(_quadFingers>=4){if(!_quadMoved&&self.onFourFingerTap)self.onFourFingerTap();}   // appui 4 doigts : réglages
    else if(_quadMoved&&up>90&&side<up){if(self.onThreeFingerSwipeUp)self.onThreeFingerSwipeUp();}   // 3 doigts vers le haut : fenêtre ⇄ iPad
    else if(!_quadMoved){[self ripple:_centroidStart color:UIColor.whiteColor];if(self.isFirstResponder)[self resignFirstResponder];else [self becomeFirstResponder];}   // appui 3 doigts : clavier
   }
   return;
  }
  [self touchFrame:e];return;
 }
 if(_mode==1&&_inputMode==2&&_dragging){[self pen:@"PU" touch:nil at:_last];_dragging=NO;}
 if([self active:e].count>0)return; // on attend que tous les doigts soient levés
 [self cancelLongPress];
 if(_mode==1&&_inputMode!=2){
  if(_dragging)[self send:'U' at:_last];
  else if(!cancelled&&!_longFired){[self send:'D' at:_start];[self send:'U' at:_start];}
 } else if(_mode==2&&!cancelled){
  if(!_moved)[self send:'R' at:_centroid];
  else if(_inertiaEnabled&&!_pinching&&fabs(_velocity)>300&&e.timestamp-_lastMoveTime<.08){
   _inertia=[CADisplayLink displayLinkWithTarget:self selector:@selector(inertiaTick:)];
   [_inertia addToRunLoop:NSRunLoop.mainRunLoop forMode:NSRunLoopCommonModes];
  }
 } else if(_mode==4&&!_moved&&!cancelled){
  if(self.onFourFingerTap)self.onFourFingerTap();
 } else if(_mode==3&&_moved&&!cancelled&&_centroidStart.y-_centroid.y>90&&fabs(_centroid.x-_centroidStart.x)<_centroidStart.y-_centroid.y){
  if(self.onThreeFingerSwipeUp)self.onThreeFingerSwipeUp();
 } else if(_mode==3&&!_moved&&!cancelled){
  [self ripple:_centroidStart color:UIColor.whiteColor];
  if(self.isFirstResponder)[self resignFirstResponder];else [self becomeFirstResponder];
 }
 _mode=0;_dragging=NO;_primary=nil;
}
- (void)touchesEnded:(NSSet<UITouch*>*)touches withEvent:(UIEvent*)e {(void)touches;[self finish:e cancelled:NO];}
- (void)touchesCancelled:(NSSet<UITouch*>*)touches withEvent:(UIEvent*)e {(void)touches;[self finish:e cancelled:YES];}

// --- Clavier : la vue elle-même reçoit la frappe -------------------------------------
- (BOOL)canBecomeFirstResponder {return YES;}
- (BOOL)hasText {return YES;}   // la touche Effacer reste toujours active
- (void)insertText:(NSString*)text {
 static NSCharacterSet *safe;
 if(!safe)safe=[NSCharacterSet characterSetWithCharactersInString:@"abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.~"];
 NSString *enc=[text stringByAddingPercentEncodingWithAllowedCharacters:safe];
 if(enc.length)[self out:[NSString stringWithFormat:@"T %@\n",enc]];
}
- (void)deleteBackward {[self out:@"K BACK\n"];}
- (UIKeyboardAppearance)keyboardAppearance {return UIKeyboardAppearanceDark;}
- (UITextAutocorrectionType)autocorrectionType {return UITextAutocorrectionTypeNo;}
- (UITextAutocapitalizationType)autocapitalizationType {return UITextAutocapitalizationTypeNone;}
- (UITextSpellCheckingType)spellCheckingType {return UITextSpellCheckingTypeNo;}
@end

#pragma mark - Webcam et micro

// Caméra de l'iPad encodée en H.264 par la puce (VideoToolbox), envoyée au PC (trame 3, flux Annex B)
@interface MPCamera : NSObject<AVCaptureVideoDataOutputSampleBufferDelegate>
@property(nonatomic,copy) void (^onFrame)(NSData *annexB);
@property(nonatomic,readonly) AVCaptureSession *session;
@property(nonatomic,readonly) BOOL front;
- (BOOL)start:(BOOL)front;
- (void)stop;
@end
static void MPEncoded(void *ref,void *src,OSStatus st,VTEncodeInfoFlags flags,CMSampleBufferRef sb){
 (void)src;(void)flags;
 if(st!=noErr||!sb||!CMSampleBufferDataIsReady(sb))return;
 MPCamera *cam=(__bridge MPCamera*)ref;
 static const uint8_t sc[4]={0,0,0,1};
 NSMutableData *out=[NSMutableData data];
 BOOL key=YES;
 CFArrayRef att=CMSampleBufferGetSampleAttachmentsArray(sb,false);
 if(att&&CFArrayGetCount(att)>0)key=!CFDictionaryContainsKey((CFDictionaryRef)CFArrayGetValueAtIndex(att,0),kCMSampleAttachmentKey_NotSync);
 if(key){   // SPS/PPS devant chaque image clé : le PC peut prendre le flux en route
  CMFormatDescriptionRef fmt=CMSampleBufferGetFormatDescription(sb);size_t count=0;
  CMVideoFormatDescriptionGetH264ParameterSetAtIndex(fmt,0,NULL,NULL,&count,NULL);
  for(size_t i=0;i<count;i++){const uint8_t *ps=NULL;size_t len=0;
   if(CMVideoFormatDescriptionGetH264ParameterSetAtIndex(fmt,i,&ps,&len,NULL,NULL)==noErr){[out appendBytes:sc length:4];[out appendBytes:ps length:len];}}
 }
 CMBlockBufferRef bb=CMSampleBufferGetDataBuffer(sb);size_t total=0;char *ptr=NULL;
 if(CMBlockBufferGetDataPointer(bb,0,NULL,&total,&ptr)!=kCMBlockBufferNoErr)return;
 size_t off=0;
 while(off+4<=total){   // AVCC (longueur sur 4 octets) -> Annex B (code de début)
  uint32_t n;memcpy(&n,ptr+off,4);n=CFSwapInt32BigToHost(n);off+=4;
  if(off+n>total)break;
  [out appendBytes:sc length:4];[out appendBytes:ptr+off length:n];off+=n;
 }
 if(cam.onFrame)cam.onFrame(out);
}
@implementation MPCamera {
 VTCompressionSessionRef _vt;
 dispatch_queue_t _q;
}
- (BOOL)start:(BOOL)front {
 [self stop];
 _front=front;
 AVCaptureDevice *dev=[AVCaptureDevice defaultDeviceWithDeviceType:AVCaptureDeviceTypeBuiltInWideAngleCamera mediaType:AVMediaTypeVideo position:front?AVCaptureDevicePositionFront:AVCaptureDevicePositionBack];
 NSError *err=nil;AVCaptureDeviceInput *in=dev?[AVCaptureDeviceInput deviceInputWithDevice:dev error:&err]:nil;
 if(!in){NSLog(@"MirrorPad: caméra indisponible %@",err);return NO;}
 _session=[AVCaptureSession new];
 if([_session canSetSessionPreset:AVCaptureSessionPreset1280x720])_session.sessionPreset=AVCaptureSessionPreset1280x720;
 if(![_session canAddInput:in])return NO;
 [_session addInput:in];
 AVCaptureVideoDataOutput *out=[AVCaptureVideoDataOutput new];
 out.videoSettings=@{(id)kCVPixelBufferPixelFormatTypeKey:@(kCVPixelFormatType_420YpCbCr8BiPlanarFullRange)};
 out.alwaysDiscardsLateVideoFrames=YES;
 _q=dispatch_queue_create("mirrorpad.camera",DISPATCH_QUEUE_SERIAL);
 [out setSampleBufferDelegate:self queue:_q];
 if(![_session canAddOutput:out])return NO;
 [_session addOutput:out];
 AVCaptureConnection *c=[out connectionWithMediaType:AVMediaTypeVideo];
 UIInterfaceOrientation o=UIApplication.sharedApplication.statusBarOrientation;
 if(c.isVideoOrientationSupported)c.videoOrientation=o==UIInterfaceOrientationLandscapeLeft?AVCaptureVideoOrientationLandscapeLeft:AVCaptureVideoOrientationLandscapeRight;
 // encodeur matériel H.264 : temps réel, sans images B, une image clé par seconde
 if(VTCompressionSessionCreate(NULL,1280,720,kCMVideoCodecType_H264,NULL,NULL,NULL,MPEncoded,(__bridge void*)self,&_vt)!=noErr){_session=nil;return NO;}
 VTSessionSetProperty(_vt,kVTCompressionPropertyKey_RealTime,kCFBooleanTrue);
 VTSessionSetProperty(_vt,kVTCompressionPropertyKey_ProfileLevel,kVTProfileLevel_H264_Main_AutoLevel);
 VTSessionSetProperty(_vt,kVTCompressionPropertyKey_AllowFrameReordering,kCFBooleanFalse);
 VTSessionSetProperty(_vt,kVTCompressionPropertyKey_MaxKeyFrameInterval,(__bridge CFTypeRef)@30);
 VTSessionSetProperty(_vt,kVTCompressionPropertyKey_ExpectedFrameRate,(__bridge CFTypeRef)@30);
 VTSessionSetProperty(_vt,kVTCompressionPropertyKey_AverageBitRate,(__bridge CFTypeRef)@3000000);
 VTCompressionSessionPrepareToEncodeFrames(_vt);
 [_session startRunning];
 return YES;
}
- (void)captureOutput:(AVCaptureOutput*)o didOutputSampleBuffer:(CMSampleBufferRef)sb fromConnection:(AVCaptureConnection*)c {
 (void)o;(void)c;
 if(!_vt)return;
 VTCompressionSessionEncodeFrame(_vt,CMSampleBufferGetImageBuffer(sb),CMSampleBufferGetPresentationTimeStamp(sb),kCMTimeInvalid,NULL,NULL,NULL);
}
- (void)stop {
 [_session stopRunning];_session=nil;
 if(_vt){VTCompressionSessionCompleteFrames(_vt,kCMTimeInvalid);VTCompressionSessionInvalidate(_vt);CFRelease(_vt);_vt=NULL;}
}
@end

// Micro de l'iPad -> PCM 16 bits mono 48 kHz (trame 6), joué sur le PC dans VB-CABLE
// Haut-parleur : joue le son du PC (PCM 16 bits stéréo) avec un petit tampon anti-saccades (60 à 200 ms)
@interface MPSpeaker : NSObject
- (void)startRate:(double)rate;
- (void)feed:(NSData*)pcm;
- (void)stop;
@property(nonatomic,readonly) BOOL running;
@end
@implementation MPSpeaker { AudioQueueRef _q; NSMutableData *_ring; NSLock *_lock; double _rate; BOOL _primed; }
static void MPSpeakerCB(void *ud,AudioQueueRef q,AudioQueueBufferRef b){
 MPSpeaker *s=(__bridge MPSpeaker*)ud;[s fill:b];AudioQueueEnqueueBuffer(q,b,0,NULL);
}
- (BOOL)running {return _q!=NULL;}
- (void)fill:(AudioQueueBufferRef)b {
 UInt32 want=b->mAudioDataBytesCapacity;NSUInteger n=0;
 [_lock lock];
 NSUInteger prime=(NSUInteger)(_rate*4*0.06)&~3u,maxBytes=(NSUInteger)(_rate*4*0.2)&~3u;
 if(_ring.length>maxBytes)[_ring replaceBytesInRange:NSMakeRange(0,(_ring.length-prime)&~3u) withBytes:NULL length:0];   // trop de retard : on rattrape
 if(!_primed&&_ring.length>=prime)_primed=YES;
 if(_primed){
  n=MIN(want,_ring.length);
  memcpy(b->mAudioData,_ring.bytes,n);
  [_ring replaceBytesInRange:NSMakeRange(0,n) withBytes:NULL length:0];
  if(n<want)_primed=NO;   // tampon vide : on attend de nouveau 60 ms de son
 }
 [_lock unlock];
 if(n<want)memset((char*)b->mAudioData+n,0,want-n);
 b->mAudioDataByteSize=want;
}
- (void)startRate:(double)rate {
 [self stop];
 _rate=rate>0?rate:48000;_ring=[NSMutableData data];_lock=[NSLock new];_primed=NO;
 Class Session=NSClassFromString(@"AVAudioSession");
 AVAudioSession *as=[Session sharedInstance];
 if(![as.category isEqualToString:@"AVAudioSessionCategoryPlayAndRecord"])   // le micro, s'il tourne, garde sa catégorie (qui joue aussi)
  [as setCategory:@"AVAudioSessionCategoryPlayback" withOptions:AVAudioSessionCategoryOptionMixWithOthers error:nil];
 [as setActive:YES error:nil];
 AudioStreamBasicDescription f={0};
 f.mSampleRate=_rate;f.mFormatID=kAudioFormatLinearPCM;f.mFormatFlags=kLinearPCMFormatFlagIsSignedInteger|kLinearPCMFormatFlagIsPacked;
 f.mChannelsPerFrame=2;f.mBitsPerChannel=16;f.mBytesPerFrame=4;f.mFramesPerPacket=1;f.mBytesPerPacket=4;
 if(AudioQueueNewOutput(&f,MPSpeakerCB,(__bridge void*)self,NULL,NULL,0,&_q)!=noErr){_q=NULL;return;}
 UInt32 bytes=((UInt32)(_rate*0.02))*4;   // tampons de 20 ms
 for(int i=0;i<3;i++){AudioQueueBufferRef b;if(AudioQueueAllocateBuffer(_q,bytes,&b)==noErr){[self fill:b];AudioQueueEnqueueBuffer(_q,b,0,NULL);}}
 AudioQueueStart(_q,NULL);
}
- (void)feed:(NSData*)pcm {
 if(!_q)return;
 [_lock lock];[_ring appendData:pcm];[_lock unlock];
}
- (void)stop {
 if(_q){AudioQueueStop(_q,true);AudioQueueDispose(_q,true);_q=NULL;}
}
@end

@interface MPMic : NSObject
@property(nonatomic,copy) void (^onPCM)(NSData *pcm);
@property(nonatomic,readonly) BOOL running;
- (BOOL)start;
- (void)stop;
@end
@implementation MPMic { AVAudioEngine *_engine; AVAudioConverter *_conv; AVAudioFormat *_target; Class _bufferClass; }
- (BOOL)start {
 [self stop];
 // Classes audio cherchées par leur nom : sur iOS 12 elles sont dans AVFoundation, sur iOS 14.5+ dans AVFAudio
 Class Session=NSClassFromString(@"AVAudioSession"),Engine=NSClassFromString(@"AVAudioEngine"),Format=NSClassFromString(@"AVAudioFormat"),Converter=NSClassFromString(@"AVAudioConverter");
 _bufferClass=NSClassFromString(@"AVAudioPCMBuffer");
 if(!Session||!Engine||!Format||!Converter||!_bufferClass)return NO;
 AVAudioSession *as=[Session sharedInstance];NSError *err=nil;
 [as setCategory:@"AVAudioSessionCategoryPlayAndRecord" withOptions:AVAudioSessionCategoryOptionDefaultToSpeaker|AVAudioSessionCategoryOptionMixWithOthers error:&err];
 [as setActive:YES error:&err];
 _engine=[Engine new];
 AVAudioInputNode *input=_engine.inputNode;
 AVAudioFormat *inFmt=[input outputFormatForBus:0];
 if(inFmt.sampleRate<=0){_engine=nil;return NO;}
 _target=[[Format alloc] initWithCommonFormat:AVAudioPCMFormatInt16 sampleRate:48000 channels:1 interleaved:YES];
 _conv=[[Converter alloc] initFromFormat:inFmt toFormat:_target];
 __weak typeof(self) w=self;
 [input installTapOnBus:0 bufferSize:1024 format:inFmt block:^(AVAudioPCMBuffer *buf,AVAudioTime *when){
  (void)when;typeof(self) s=w;if(!s)return;
  AVAudioFrameCount cap=(AVAudioFrameCount)(buf.frameLength*48000.0/inFmt.sampleRate)+32;
  AVAudioPCMBuffer *out=[[s->_bufferClass alloc] initWithPCMFormat:s->_target frameCapacity:cap];
  __block BOOL given=NO;NSError *e=nil;
  [s->_conv convertToBuffer:out error:&e withInputFromBlock:^AVAudioBuffer*(AVAudioPacketCount n,AVAudioConverterInputStatus *st){
   (void)n;if(given){*st=AVAudioConverterInputStatus_NoDataNow;return nil;}
   given=YES;*st=AVAudioConverterInputStatus_HaveData;return buf;
  }];
  if(out.frameLength&&s.onPCM)s.onPCM([NSData dataWithBytes:out.int16ChannelData[0] length:out.frameLength*2]);
 }];
 [_engine prepare];
 if(![_engine startAndReturnError:&err]){NSLog(@"MirrorPad: micro %@",err);[self stop];return NO;}
 _running=YES;
 return YES;
}
- (void)stop {
 if(_engine){[_engine.inputNode removeTapOnBus:0];[_engine stop];_engine=nil;}
 _running=NO;
}
@end

#pragma mark - Écran d'accueil

static UIColor *MPRGB(unsigned hex,CGFloat a){
 return [UIColor colorWithRed:((hex>>16)&255)/255.0 green:((hex>>8)&255)/255.0 blue:(hex&255)/255.0 alpha:a];
}
static UILabel *MPLabel(NSString *t,CGFloat size,UIFontWeight w,CGFloat alpha,CGFloat kern){
 UILabel *l=[UILabel new];
 UIFont *f=[UIFont systemFontOfSize:size weight:w];UIColor *c=[UIColor colorWithWhite:1 alpha:alpha];
 l.font=f;l.textColor=c;
 l.attributedText=[[NSAttributedString alloc] initWithString:t attributes:@{NSKernAttributeName:@(kern),NSFontAttributeName:f,NSForegroundColorAttributeName:c}];
 l.numberOfLines=0;
 return l;
}
static CABasicAnimation *MPLoop(NSString *key,id from,id to,CFTimeInterval d){
 CABasicAnimation *a=[CABasicAnimation animationWithKeyPath:key];
 a.fromValue=from;a.toValue=to;a.duration=d;a.autoreverses=YES;a.repeatCount=HUGE_VALF;
 a.timingFunction=[CAMediaTimingFunction functionWithName:kCAMediaTimingFunctionEaseInEaseOut];
 a.removedOnCompletion=NO;
 return a;
}

// Écran affiché tant que le PC n'envoie pas d'image : reprend la DA de l'icône
// (dégradé cyan → bleu → violet, courbe lumineuse, verre dépoli).
@interface MPHomeView : UIView
@property(nonatomic,readonly) UILabel *addressLabel,*lastPCLabel;
@property(nonatomic,readonly) UIButton *settingsButton;
@end
@implementation MPHomeView {
 UIStackView *_main;
 CAGradientLayer *_bg,*_orbA,*_orbB,*_cardShine;
 CAShapeLayer *_wave1,*_wave2;
 UIView *_logoWrap,*_dotView,*_card;
 CALayer *_halo;
 BOOL _animated;
}
- (instancetype)initWithFrame:(CGRect)frame {
 if(!(self=[super initWithFrame:frame]))return nil;
 self.clipsToBounds=YES;

 // Fond : dégradé de l'icône + halos lumineux + courbes
 _bg=[CAGradientLayer layer];
 _bg.colors=@[(id)MPRGB(0x16C6FF,1).CGColor,(id)MPRGB(0x3B5BFF,1).CGColor,(id)MPRGB(0x7A2EF6,1).CGColor];
 _bg.locations=@[@0,@0.52,@1];_bg.startPoint=CGPointMake(0,0);_bg.endPoint=CGPointMake(1,1);
 [self.layer addSublayer:_bg];
 _orbA=[CAGradientLayer layer];_orbA.type=@"radial";
 _orbA.colors=@[(id)MPRGB(0x7FE9FF,.55).CGColor,(id)MPRGB(0x7FE9FF,0).CGColor];
 _orbA.startPoint=CGPointMake(.5,.5);_orbA.endPoint=CGPointMake(1,1);
 [self.layer addSublayer:_orbA];
 _orbB=[CAGradientLayer layer];_orbB.type=@"radial";
 _orbB.colors=@[(id)MPRGB(0xD06BFF,.50).CGColor,(id)MPRGB(0xD06BFF,0).CGColor];
 _orbB.startPoint=CGPointMake(.5,.5);_orbB.endPoint=CGPointMake(1,1);
 [self.layer addSublayer:_orbB];
 _wave1=[CAShapeLayer layer];_wave1.fillColor=[UIColor colorWithWhite:1 alpha:.07].CGColor;[self.layer addSublayer:_wave1];
 _wave2=[CAShapeLayer layer];_wave2.fillColor=UIColor.clearColor.CGColor;_wave2.lineWidth=2;
 _wave2.strokeColor=[UIColor colorWithWhite:1 alpha:.22].CGColor;[self.layer addSublayer:_wave2];

 // Logo
 _logoWrap=[UIView new];_logoWrap.translatesAutoresizingMaskIntoConstraints=NO;
 _logoWrap.layer.shadowColor=MPRGB(0x1A0B5C,1).CGColor;_logoWrap.layer.shadowOpacity=.45;
 _logoWrap.layer.shadowRadius=34;_logoWrap.layer.shadowOffset=CGSizeMake(0,22);
 UIImageView *logo=[[UIImageView alloc] initWithImage:[UIImage imageNamed:@"Logo"]];
 logo.translatesAutoresizingMaskIntoConstraints=NO;logo.contentMode=UIViewContentModeScaleAspectFill;
 logo.layer.cornerRadius=48;logo.layer.masksToBounds=YES;
 logo.layer.borderWidth=1.5;logo.layer.borderColor=[UIColor colorWithWhite:1 alpha:.45].CGColor;
 [_logoWrap addSubview:logo];

 // Textes
 UILabel *eyebrow=MPLabel(@"ÉCRAN SUPPLÉMENTAIRE",13,UIFontWeightSemibold,.78,3);
 UILabel *title=MPLabel(@"MirrorPad",64,UIFontWeightBold,1,-1.2);
 UILabel *subtitle=MPLabel(@"Votre iPad, un écran de plus pour votre PC.",21,UIFontWeightRegular,.86,0);

 // Pastille d'état avec point qui pulse
 UIView *pill=[UIView new];pill.translatesAutoresizingMaskIntoConstraints=NO;
 pill.backgroundColor=[UIColor colorWithWhite:1 alpha:.16];pill.layer.cornerRadius=19;
 pill.layer.borderWidth=1;pill.layer.borderColor=[UIColor colorWithWhite:1 alpha:.30].CGColor;
 _dotView=[UIView new];_dotView.translatesAutoresizingMaskIntoConstraints=NO;
 _halo=[CALayer layer];_halo.frame=CGRectMake(0,0,10,10);_halo.cornerRadius=5;_halo.backgroundColor=MPRGB(0xFFC14D,1).CGColor;
 CALayer *dot=[CALayer layer];dot.frame=CGRectMake(0,0,10,10);dot.cornerRadius=5;dot.backgroundColor=MPRGB(0xFFC14D,1).CGColor;
 [_dotView.layer addSublayer:_halo];[_dotView.layer addSublayer:dot];
 UILabel *state=MPLabel(@"En attente du PC…",17,UIFontWeightMedium,1,.2);state.translatesAutoresizingMaskIntoConstraints=NO;
 [pill addSubview:_dotView];[pill addSubview:state];

 // Carte « verre » avec l'adresse
 _card=[UIView new];_card.translatesAutoresizingMaskIntoConstraints=NO;
 _card.backgroundColor=[UIColor colorWithWhite:1 alpha:.13];_card.layer.cornerRadius=26;_card.clipsToBounds=YES;
 _card.layer.borderWidth=1;_card.layer.borderColor=[UIColor colorWithWhite:1 alpha:.34].CGColor;
 _cardShine=[CAGradientLayer layer];
 _cardShine.colors=@[(id)[UIColor colorWithWhite:1 alpha:.20].CGColor,(id)[UIColor colorWithWhite:1 alpha:0].CGColor];
 _cardShine.startPoint=CGPointMake(0,0);_cardShine.endPoint=CGPointMake(.6,1);
 _cardShine.frame=CGRectMake(0,0,470,320); // la carte fait 470 de large ; le surplus est rogné
 [_card.layer addSublayer:_cardShine];
 UILabel *caption=MPLabel(@"ADRESSE DE CET IPAD",12,UIFontWeightSemibold,.72,2.4);
 _addressLabel=[UILabel new];
 _addressLabel.font=[UIFont monospacedDigitSystemFontOfSize:44 weight:UIFontWeightSemibold];
 _addressLabel.textColor=UIColor.whiteColor;_addressLabel.adjustsFontSizeToFitWidth=YES;_addressLabel.minimumScaleFactor=.6;
 UIView *sep=[UIView new];sep.backgroundColor=[UIColor colorWithWhite:1 alpha:.22];
 [sep.heightAnchor constraintEqualToConstant:1].active=YES;
 UILabel *hint=MPLabel([NSString stringWithFormat:@"Port %d  ·  Ouvrez MirrorPad sur le PC, la connexion est automatique.",MP_PORT],15,UIFontWeightRegular,.80,0);
 UIStackView *cardStack=[[UIStackView alloc] initWithArrangedSubviews:@[caption,_addressLabel,sep,hint]];
 cardStack.axis=UILayoutConstraintAxisVertical;cardStack.spacing=10;cardStack.translatesAutoresizingMaskIntoConstraints=NO;
 [cardStack setCustomSpacing:16 afterView:_addressLabel];
 [_card addSubview:cardStack];

 UIStackView *text=[[UIStackView alloc] initWithArrangedSubviews:@[eyebrow,title,subtitle,pill,_card]];
 text.axis=UILayoutConstraintAxisVertical;text.alignment=UIStackViewAlignmentLeading;text.spacing=12;
 [text setCustomSpacing:2 afterView:eyebrow];[text setCustomSpacing:22 afterView:subtitle];[text setCustomSpacing:26 afterView:pill];

 _lastPCLabel=MPLabel(@" ",15,UIFontWeightMedium,.85,.2);
 _lastPCLabel.hidden=YES;
 [text addArrangedSubview:_lastPCLabel];
 [text setCustomSpacing:14 afterView:_card];

 UIStackView *main=[[UIStackView alloc] initWithArrangedSubviews:@[_logoWrap,text]];
 main.axis=UILayoutConstraintAxisHorizontal;main.alignment=UIStackViewAlignmentCenter;main.spacing=64;
 main.translatesAutoresizingMaskIntoConstraints=NO;
 _main=main;
 [self addSubview:main];

 // Bouton « Réglages » (verre) en haut à droite
 _settingsButton=[UIButton buttonWithType:UIButtonTypeSystem];
 _settingsButton.translatesAutoresizingMaskIntoConstraints=NO;
 [_settingsButton setTitle:@"Réglages" forState:UIControlStateNormal];
 [_settingsButton setTitleColor:UIColor.whiteColor forState:UIControlStateNormal];
 _settingsButton.titleLabel.font=[UIFont systemFontOfSize:16 weight:UIFontWeightSemibold];
 _settingsButton.backgroundColor=[UIColor colorWithWhite:1 alpha:.16];
 _settingsButton.layer.cornerRadius=20;_settingsButton.layer.borderWidth=1;
 _settingsButton.layer.borderColor=[UIColor colorWithWhite:1 alpha:.32].CGColor;
 _settingsButton.contentEdgeInsets=UIEdgeInsetsMake(9,20,9,20);
 [self addSubview:_settingsButton];

 // Pied de page
 UILabel *gestures=MPLabel(@"1 doigt : clic   ·   glisser : sélectionner   ·   2 doigts : clic droit et défilement",14,UIFontWeightMedium,.72,.3);
 gestures.textAlignment=NSTextAlignmentCenter;gestures.translatesAutoresizingMaskIntoConstraints=NO;
 [self addSubview:gestures];
 NSString *v=[NSBundle.mainBundle objectForInfoDictionaryKey:@"CFBundleShortVersionString"]?:@"";
 UILabel *version=MPLabel([@"v" stringByAppendingString:v],13,UIFontWeightMedium,.5,.5);
 version.translatesAutoresizingMaskIntoConstraints=NO;[self addSubview:version];

 [NSLayoutConstraint activateConstraints:@[
  [_logoWrap.widthAnchor constraintEqualToConstant:220],[_logoWrap.heightAnchor constraintEqualToConstant:220],
  [logo.leadingAnchor constraintEqualToAnchor:_logoWrap.leadingAnchor],[logo.trailingAnchor constraintEqualToAnchor:_logoWrap.trailingAnchor],
  [logo.topAnchor constraintEqualToAnchor:_logoWrap.topAnchor],[logo.bottomAnchor constraintEqualToAnchor:_logoWrap.bottomAnchor],
  [pill.heightAnchor constraintEqualToConstant:38],
  [_dotView.widthAnchor constraintEqualToConstant:10],[_dotView.heightAnchor constraintEqualToConstant:10],
  [_dotView.leadingAnchor constraintEqualToAnchor:pill.leadingAnchor constant:16],[_dotView.centerYAnchor constraintEqualToAnchor:pill.centerYAnchor],
  [state.leadingAnchor constraintEqualToAnchor:_dotView.trailingAnchor constant:10],[state.centerYAnchor constraintEqualToAnchor:pill.centerYAnchor],
  [state.trailingAnchor constraintEqualToAnchor:pill.trailingAnchor constant:-18],
  [_card.widthAnchor constraintEqualToConstant:470],
  [cardStack.topAnchor constraintEqualToAnchor:_card.topAnchor constant:24],[cardStack.bottomAnchor constraintEqualToAnchor:_card.bottomAnchor constant:-24],
  [cardStack.leadingAnchor constraintEqualToAnchor:_card.leadingAnchor constant:28],[cardStack.trailingAnchor constraintEqualToAnchor:_card.trailingAnchor constant:-28],
  [main.centerXAnchor constraintEqualToAnchor:self.centerXAnchor],[main.centerYAnchor constraintEqualToAnchor:self.centerYAnchor constant:-18],
  [gestures.centerXAnchor constraintEqualToAnchor:self.centerXAnchor],[gestures.bottomAnchor constraintEqualToAnchor:self.bottomAnchor constant:-30],
  [version.trailingAnchor constraintEqualToAnchor:self.trailingAnchor constant:-28],[version.bottomAnchor constraintEqualToAnchor:self.bottomAnchor constant:-30],
  [_settingsButton.trailingAnchor constraintEqualToAnchor:self.trailingAnchor constant:-28],[_settingsButton.topAnchor constraintEqualToAnchor:self.topAnchor constant:28],
 ]];
 return self;
}
- (void)layoutSubviews {
 [super layoutSubviews];
 CGFloat w=self.bounds.size.width,h=self.bounds.size.height;
 // paysage : logo à gauche du texte ; portrait : logo au-dessus
 UILayoutConstraintAxis axis=h>w?UILayoutConstraintAxisVertical:UILayoutConstraintAxisHorizontal;
 if(_main.axis!=axis){_main.axis=axis;_main.spacing=h>w?40:64;}
 [CATransaction begin];[CATransaction setDisableActions:YES];
 _bg.frame=self.bounds;
 _orbA.frame=CGRectMake(-w*.18,-h*.35,w*.75,w*.75);
 _orbB.frame=CGRectMake(w*.48,h*.30,w*.75,w*.75);
 UIBezierPath *p=[UIBezierPath bezierPath];
 [p moveToPoint:CGPointMake(0,h*.78)];
 [p addCurveToPoint:CGPointMake(w,h*.30) controlPoint1:CGPointMake(w*.35,h*.98) controlPoint2:CGPointMake(w*.62,h*.30)];
 [p addLineToPoint:CGPointMake(w,h)];[p addLineToPoint:CGPointMake(0,h)];[p closePath];
 _wave1.path=p.CGPath;
 UIBezierPath *l=[UIBezierPath bezierPath];
 [l moveToPoint:CGPointMake(-20,h*.70)];
 [l addCurveToPoint:CGPointMake(w+20,h*.20) controlPoint1:CGPointMake(w*.30,h*.95) controlPoint2:CGPointMake(w*.60,h*.18)];
 _wave2.path=l.CGPath;
 _logoWrap.layer.shadowPath=[UIBezierPath bezierPathWithRoundedRect:_logoWrap.bounds cornerRadius:48].CGPath;
 [CATransaction commit];
}
- (void)didMoveToWindow {
 [super didMoveToWindow];
 if(!self.window||_animated)return;
 _animated=YES;
 [_logoWrap.layer addAnimation:MPLoop(@"transform.translation.y",@0,@-10,3.2) forKey:@"float"];
 [_orbA addAnimation:MPLoop(@"transform.translation",[NSValue valueWithCGSize:CGSizeZero],[NSValue valueWithCGSize:CGSizeMake(70,50)],9) forKey:@"drift"];
 [_orbB addAnimation:MPLoop(@"transform.translation",[NSValue valueWithCGSize:CGSizeZero],[NSValue valueWithCGSize:CGSizeMake(-80,-40)],11) forKey:@"drift"];
 [_bg addAnimation:MPLoop(@"colors",_bg.colors,@[(id)MPRGB(0x22B4FF,1).CGColor,(id)MPRGB(0x4A4DFF,1).CGColor,(id)MPRGB(0x8A2BEF,1).CGColor],8) forKey:@"shift"];
 [_wave2 addAnimation:MPLoop(@"opacity",@1,@.35,4) forKey:@"glow"];
 CABasicAnimation *grow=[CABasicAnimation animationWithKeyPath:@"transform.scale"];grow.fromValue=@1;grow.toValue=@2.8;
 CABasicAnimation *fade=[CABasicAnimation animationWithKeyPath:@"opacity"];fade.fromValue=@.85;fade.toValue=@0;
 CAAnimationGroup *pulse=[CAAnimationGroup animation];pulse.animations=@[grow,fade];pulse.duration=1.6;
 pulse.repeatCount=HUGE_VALF;pulse.removedOnCompletion=NO;
 pulse.timingFunction=[CAMediaTimingFunction functionWithName:kCAMediaTimingFunctionEaseOut];
 [_halo addAnimation:pulse forKey:@"pulse"];
}
@end

#pragma mark - Réglages et indicateur

static NSUserDefaults *MPDefaults(void){
 NSUserDefaults *d=NSUserDefaults.standardUserDefaults;
 [d registerDefaults:@{@"longPress":@YES,@"inertia":@YES,@"hud":@NO,@"fps":@30,@"mbps":@12,@"inputMode":@0,@"penEraser":@NO,@"clipboard":@YES,@"pcSound":@YES,@"touchBar":@YES,@"touchBarFolded":@NO}];
 return d;
}
static NSString *MPAgo(NSDate *date){
 NSTimeInterval t=-date.timeIntervalSinceNow;
 if(t<90)return @"à l'instant";
 if(t<3600)return [NSString stringWithFormat:@"il y a %d min",(int)(t/60)];
 if(t<86400)return [NSString stringWithFormat:@"il y a %d h",(int)(t/3600)];
 return [NSString stringWithFormat:@"il y a %d j",(int)(t/86400)];
}

// Panneau « verre » ouvert par le bouton Réglages ou un tap à quatre doigts.
@interface MPSettingsPanel : UIView
@property(nonatomic,copy) void (^onQuality)(int fps,int mbps);
@property(nonatomic,copy) void (^onChange)(void);
@property(nonatomic,copy) void (^onTrackpad)(void);
@property(nonatomic,copy) void (^onShortcuts)(void);
@property(nonatomic,copy) void (^onKeyboard)(void);
@property(nonatomic,copy) void (^onCamera)(void);
@property(nonatomic,copy) void (^onMic)(void);
@property(nonatomic,copy) void (^onPhotos)(void);
@property(nonatomic,copy) void (^onFiles)(void);
@property(nonatomic,copy) void (^onDashboard)(void);
- (void)showIn:(UIView*)parent;
@end
@implementation MPSettingsPanel {
 UISegmentedControl *_fps,*_quality,*_inputMode,*_penTool;
 UISwitch *_longPress,*_inertia,*_hud,*_clip,*_sound,*_bar;
 UIStackView *_history,*_paired;
}
static const int MPQualities[3]={6,12,20};
- (UILabel*)section:(NSString*)t {UILabel *l=MPLabel(t,12,UIFontWeightSemibold,.6,2);return l;}
- (UIView*)row:(NSString*)title control:(UIView*)c {
 UILabel *l=MPLabel(title,17,UIFontWeightMedium,1,0);
 UIStackView *r=[[UIStackView alloc] initWithArrangedSubviews:@[l,c]];
 r.axis=UILayoutConstraintAxisHorizontal;r.alignment=UIStackViewAlignmentCenter;r.distribution=UIStackViewDistributionEqualSpacing;
 return r;
}
- (UISegmentedControl*)segments:(NSArray*)items {
 UISegmentedControl *sc=[[UISegmentedControl alloc] initWithItems:items];
 sc.tintColor=UIColor.whiteColor;
 [sc addTarget:self action:@selector(qualityChanged) forControlEvents:UIControlEventValueChanged];
 return sc;
}
- (UIButton*)tool:(NSString*)title action:(SEL)sel {
 UIButton *b=[UIButton buttonWithType:UIButtonTypeSystem];
 [b setTitle:title forState:UIControlStateNormal];[b setTitleColor:UIColor.whiteColor forState:UIControlStateNormal];
 b.titleLabel.font=[UIFont systemFontOfSize:16 weight:UIFontWeightSemibold];
 b.backgroundColor=[UIColor colorWithWhite:1 alpha:.14];b.layer.cornerRadius=14;
 b.layer.borderWidth=1;b.layer.borderColor=[UIColor colorWithWhite:1 alpha:.28].CGColor;
 [b.heightAnchor constraintEqualToConstant:46].active=YES;
 [b addTarget:self action:sel forControlEvents:UIControlEventTouchUpInside];
 return b;
}
- (void)openTrackpad {[self hide];if(self.onTrackpad)self.onTrackpad();}
- (void)openShortcuts {[self hide];if(self.onShortcuts)self.onShortcuts();}
- (void)openKeyboard {[self hide];if(self.onKeyboard)self.onKeyboard();}
- (void)openCamera {[self hide];if(self.onCamera)self.onCamera();}
- (void)toggleMic {[self hide];if(self.onMic)self.onMic();}
- (void)openPhotos {[self hide];if(self.onPhotos)self.onPhotos();}
- (void)openFiles {[self hide];if(self.onFiles)self.onFiles();}
- (void)openDashboard {[self hide];if(self.onDashboard)self.onDashboard();}
- (void)modeChanged {
 NSUserDefaults *d=MPDefaults();
 [d setInteger:_inputMode.selectedSegmentIndex forKey:@"inputMode"];
 [d setBool:_penTool.selectedSegmentIndex==1 forKey:@"penEraser"];
 if(self.onChange)self.onChange();
}
- (UISwitch*)toggle {
 UISwitch *sw=[UISwitch new];sw.onTintColor=MPRGB(0x4A6BFF,1);
 [sw addTarget:self action:@selector(togglesChanged) forControlEvents:UIControlEventValueChanged];
 return sw;
}
- (instancetype)initWithFrame:(CGRect)f {
 if(!(self=[super initWithFrame:f]))return nil;
 self.backgroundColor=[UIColor colorWithRed:.02 green:.03 blue:.12 alpha:.55];
 [self addGestureRecognizer:[[UITapGestureRecognizer alloc] initWithTarget:self action:@selector(backgroundTap:)]];

 UIVisualEffectView *card=[[UIVisualEffectView alloc] initWithEffect:[UIBlurEffect effectWithStyle:UIBlurEffectStyleDark]];
 card.translatesAutoresizingMaskIntoConstraints=NO;card.layer.cornerRadius=28;card.clipsToBounds=YES;
 card.layer.borderWidth=1;card.layer.borderColor=[UIColor colorWithWhite:1 alpha:.22].CGColor;
 [self addSubview:card];
 CAGradientLayer *tint=[CAGradientLayer layer];
 tint.colors=@[(id)MPRGB(0x16C6FF,.30).CGColor,(id)MPRGB(0x3B5BFF,.25).CGColor,(id)MPRGB(0x7A2EF6,.32).CGColor];
 tint.startPoint=CGPointMake(0,0);tint.endPoint=CGPointMake(1,1);tint.frame=CGRectMake(0,0,560,900);
 [card.contentView.layer addSublayer:tint];

 UILabel *title=MPLabel(@"Réglages",28,UIFontWeightBold,1,-.4);
 UIButton *close=[UIButton buttonWithType:UIButtonTypeSystem];
 [close setTitle:@"Fermer" forState:UIControlStateNormal];[close setTitleColor:UIColor.whiteColor forState:UIControlStateNormal];
 close.titleLabel.font=[UIFont systemFontOfSize:16 weight:UIFontWeightSemibold];
 [close addTarget:self action:@selector(hide) forControlEvents:UIControlEventTouchUpInside];
 UIStackView *head=[[UIStackView alloc] initWithArrangedSubviews:@[title,close]];
 head.distribution=UIStackViewDistributionEqualSpacing;head.alignment=UIStackViewAlignmentCenter;

 _inputMode=[[UISegmentedControl alloc] initWithItems:@[@"Souris",@"Tactile",@"Stylet"]];
 _penTool=[[UISegmentedControl alloc] initWithItems:@[@"Stylo",@"Gomme"]];
 for(UISegmentedControl *sc in @[_inputMode,_penTool]){sc.tintColor=UIColor.whiteColor;[sc addTarget:self action:@selector(modeChanged) forControlEvents:UIControlEventValueChanged];}
 UILabel *modeNote=MPLabel(@"Souris : gestes MirrorPad · Tactile : écran tactile Windows natif · Stylet : tablette graphique (Windows Ink).",13,UIFontWeightRegular,.6,0);
 UIStackView *tools=[[UIStackView alloc] initWithArrangedSubviews:@[[self tool:@"Pavé tactile" action:@selector(openTrackpad)],[self tool:@"Raccourcis" action:@selector(openShortcuts)],[self tool:@"Clavier" action:@selector(openKeyboard)],[self tool:@"Tableau de bord" action:@selector(openDashboard)]]];
 tools.distribution=UIStackViewDistributionFillEqually;tools.spacing=10;
 UIStackView *sensors=[[UIStackView alloc] initWithArrangedSubviews:@[[self tool:@"Webcam" action:@selector(openCamera)],[self tool:@"Micro" action:@selector(toggleMic)],[self tool:@"Envoyer des photos" action:@selector(openPhotos)],[self tool:@"Envoyer un fichier" action:@selector(openFiles)]]];
 sensors.distribution=UIStackViewDistributionFillEqually;sensors.spacing=10;
 _fps=[self segments:@[@"30 i/s",@"60 i/s"]];
 _quality=[self segments:@[@"Éco",@"Standard",@"Max"]];
 _longPress=[self toggle];_inertia=[self toggle];_hud=[self toggle];_clip=[self toggle];_sound=[self toggle];_bar=[self toggle];
 UILabel *note=MPLabel(@"La qualité est appliquée au PC dès qu'il est connecté.",13,UIFontWeightRegular,.6,0);
 _history=[UIStackView new];_history.axis=UILayoutConstraintAxisVertical;_history.spacing=6;
 _paired=[UIStackView new];_paired.axis=UILayoutConstraintAxisVertical;_paired.spacing=8;_paired.alignment=UIStackViewAlignmentLeading;

 UIStackView *st=[[UIStackView alloc] initWithArrangedSubviews:@[head,
   [self section:@"MODE"],[self row:@"Le doigt agit comme" control:_inputMode],[self row:@"Outil du stylet" control:_penTool],modeNote,tools,
   [self section:@"CAPTEURS DE L'IPAD"],sensors,
   [self section:@"PARTAGE"],[self row:@"Presse-papiers partagé avec le PC" control:_clip],[self row:@"Son du PC sur l'iPad" control:_sound],
   [self section:@"IMAGE"],[self row:@"Fluidité" control:_fps],[self row:@"Qualité" control:_quality],note,
   [self section:@"GESTES"],[self row:@"Appui long = clic droit" control:_longPress],[self row:@"Défilement avec élan" control:_inertia],
   [self section:@"AFFICHAGE"],[self row:@"Touch Bar (raccourcis de l'appli active)" control:_bar],[self row:@"Indicateur de performance" control:_hud],
   [self section:@"DERNIERS PC"],_history,
   [self section:@"SÉCURITÉ"],_paired]];
 st.axis=UILayoutConstraintAxisVertical;st.spacing=12;st.translatesAutoresizingMaskIntoConstraints=NO;
 [st setCustomSpacing:22 afterView:head];[st setCustomSpacing:16 afterView:tools];[st setCustomSpacing:16 afterView:sensors];
 for(UIView *v in st.arrangedSubviews)if([v isKindOfClass:UILabel.class]&&((UILabel*)v).font.pointSize==12)[st setCustomSpacing:8 afterView:v];
 // le contenu défile si l'écran est trop petit (portrait, clavier…)
 UIScrollView *scroll=[UIScrollView new];scroll.translatesAutoresizingMaskIntoConstraints=NO;
 scroll.indicatorStyle=UIScrollViewIndicatorStyleWhite;scroll.alwaysBounceVertical=YES;
 [card.contentView addSubview:scroll];[scroll addSubview:st];
 NSLayoutConstraint *fit=[card.heightAnchor constraintEqualToAnchor:st.heightAnchor constant:52];fit.priority=UILayoutPriorityDefaultLow;
 [NSLayoutConstraint activateConstraints:@[
  [card.centerXAnchor constraintEqualToAnchor:self.centerXAnchor],[card.centerYAnchor constraintEqualToAnchor:self.centerYAnchor],
  [card.widthAnchor constraintEqualToConstant:520],fit,
  [card.heightAnchor constraintLessThanOrEqualToAnchor:self.heightAnchor constant:-48],
  [scroll.topAnchor constraintEqualToAnchor:card.contentView.topAnchor],[scroll.bottomAnchor constraintEqualToAnchor:card.contentView.bottomAnchor],
  [scroll.leadingAnchor constraintEqualToAnchor:card.contentView.leadingAnchor],[scroll.trailingAnchor constraintEqualToAnchor:card.contentView.trailingAnchor],
  [st.topAnchor constraintEqualToAnchor:scroll.contentLayoutGuide.topAnchor constant:26],[st.bottomAnchor constraintEqualToAnchor:scroll.contentLayoutGuide.bottomAnchor constant:-26],
  [st.leadingAnchor constraintEqualToAnchor:scroll.contentLayoutGuide.leadingAnchor constant:30],[st.trailingAnchor constraintEqualToAnchor:scroll.contentLayoutGuide.trailingAnchor constant:-30],
  [st.widthAnchor constraintEqualToAnchor:scroll.frameLayoutGuide.widthAnchor constant:-60],
 ]];
 return self;
}
- (void)backgroundTap:(UITapGestureRecognizer*)g {
 UIView *hit=[self hitTest:[g locationInView:self] withEvent:nil];
 if(hit==self)[self hide];
}
- (void)load {
 NSUserDefaults *d=MPDefaults();
 _fps.selectedSegmentIndex=[d integerForKey:@"fps"]>=60?1:0;
 NSInteger m=[d integerForKey:@"mbps"];_quality.selectedSegmentIndex=m<=8?0:(m<=15?1:2);
 _inputMode.selectedSegmentIndex=[d integerForKey:@"inputMode"];_penTool.selectedSegmentIndex=[d boolForKey:@"penEraser"]?1:0;
 _longPress.on=[d boolForKey:@"longPress"];_clip.on=[d boolForKey:@"clipboard"];_sound.on=[d boolForKey:@"pcSound"];_inertia.on=[d boolForKey:@"inertia"];_hud.on=[d boolForKey:@"hud"];_bar.on=[d boolForKey:@"touchBar"];
 for(UIView *v in _history.arrangedSubviews){[_history removeArrangedSubview:v];[v removeFromSuperview];}
 NSArray *h=[d arrayForKey:@"history"];
 if(!h.count)[_history addArrangedSubview:MPLabel(@"Aucun PC pour l'instant.",15,UIFontWeightRegular,.6,0)];
 for(NSDictionary *e in h)
  [_history addArrangedSubview:MPLabel([NSString stringWithFormat:@"%@  ·  %@  ·  %@",e[@"name"],e[@"ip"],MPAgo(e[@"date"])],15,UIFontWeightRegular,.85,0)];
 for(UIView *v in _paired.arrangedSubviews){[_paired removeArrangedSubview:v];[v removeFromSuperview];}
 NSDictionary *pcs=MPPairedPCs();
 NSMutableArray *names=[NSMutableArray array];for(NSDictionary *v in pcs.allValues)[names addObject:v[@"name"]?:@"PC"];
 [_paired addArrangedSubview:MPLabel(pcs.count?[NSString stringWithFormat:@"PC autorisés (connexion chiffrée) : %@",[names componentsJoinedByString:@", "]]:@"Aucun PC appairé.",15,UIFontWeightRegular,.85,0)];
 if(pcs.count){
  UIButton *b=[UIButton buttonWithType:UIButtonTypeSystem];
  [b setTitle:@"Oublier tous les PC" forState:UIControlStateNormal];[b setTitleColor:MPRGB(0xFF8A8A,1) forState:UIControlStateNormal];
  b.titleLabel.font=[UIFont systemFontOfSize:16 weight:UIFontWeightSemibold];
  [b addTarget:self action:@selector(forgetPCs) forControlEvents:UIControlEventTouchUpInside];
  [_paired addArrangedSubview:b];
 }
}
- (void)forgetPCs {
 [NSUserDefaults.standardUserDefaults removeObjectForKey:@"pairedPCs"];
 [self load];
}
- (void)qualityChanged {
 int fps=_fps.selectedSegmentIndex==1?60:30,mbps=MPQualities[MAX(0,_quality.selectedSegmentIndex)];
 NSUserDefaults *d=MPDefaults();[d setInteger:fps forKey:@"fps"];[d setInteger:mbps forKey:@"mbps"];
 if(self.onQuality)self.onQuality(fps,mbps);
}
- (void)togglesChanged {
 NSUserDefaults *d=MPDefaults();
 [d setBool:_longPress.on forKey:@"longPress"];[d setBool:_clip.on forKey:@"clipboard"];[d setBool:_sound.on forKey:@"pcSound"];[d setBool:_inertia.on forKey:@"inertia"];[d setBool:_hud.on forKey:@"hud"];[d setBool:_bar.on forKey:@"touchBar"];
 if(self.onChange)self.onChange();
}
- (void)showIn:(UIView*)parent {
 [self load];
 self.frame=parent.bounds;self.autoresizingMask=UIViewAutoresizingFlexibleWidth|UIViewAutoresizingFlexibleHeight;
 self.alpha=0;self.transform=CGAffineTransformMakeScale(1.03,1.03);
 [parent addSubview:self];
 [UIView animateWithDuration:.3 animations:^{self.alpha=1;self.transform=CGAffineTransformIdentity;}];
}
- (void)hide {
 [UIView animateWithDuration:.25 animations:^{self.alpha=0;} completion:^(BOOL done){(void)done;[self removeFromSuperview];}];
}
@end

// Carte « verre » au centre de l'écran : code d'appairage, résultat, alertes de sécurité
@interface MPNoticeView : UIView
- (instancetype)initWithTitle:(NSString*)title text:(NSString*)text code:(NSString*)code ok:(BOOL)ok;
- (void)showIn:(UIView*)parent autoHide:(NSTimeInterval)delay;
- (void)hide;
@end
@implementation MPNoticeView
- (instancetype)initWithTitle:(NSString*)title text:(NSString*)text code:(NSString*)code ok:(BOOL)ok {
 if(!(self=[super initWithFrame:CGRectZero]))return nil;
 self.backgroundColor=[UIColor colorWithRed:.02 green:.03 blue:.12 alpha:.55];
 UIVisualEffectView *card=[[UIVisualEffectView alloc] initWithEffect:[UIBlurEffect effectWithStyle:UIBlurEffectStyleDark]];
 card.translatesAutoresizingMaskIntoConstraints=NO;card.layer.cornerRadius=28;card.clipsToBounds=YES;
 card.layer.borderWidth=1;card.layer.borderColor=[UIColor colorWithWhite:1 alpha:.22].CGColor;
 [self addSubview:card];
 CAGradientLayer *tint=[CAGradientLayer layer];
 tint.colors=@[(id)MPRGB(0x16C6FF,.30).CGColor,(id)MPRGB(0x3B5BFF,.25).CGColor,(id)MPRGB(0x7A2EF6,.32).CGColor];
 tint.startPoint=CGPointMake(0,0);tint.endPoint=CGPointMake(1,1);tint.frame=CGRectMake(0,0,560,600);
 [card.contentView.layer addSublayer:tint];
 UILabel *badge=MPLabel(code?@"APPAIRAGE":(ok?@"C'EST FAIT":@"SÉCURITÉ"),12,UIFontWeightSemibold,.7,2.4);
 UILabel *t=MPLabel(title,28,UIFontWeightBold,1,-.4);
 UILabel *m=MPLabel(text,17,UIFontWeightRegular,.86,0);
 NSMutableArray *items=[NSMutableArray arrayWithObjects:badge,t,m,nil];
 if(code){
  UILabel *c=[UILabel new];
  c.attributedText=[[NSAttributedString alloc] initWithString:[NSString stringWithFormat:@"%@ %@",[code substringToIndex:3],[code substringFromIndex:3]]
   attributes:@{NSFontAttributeName:[UIFont monospacedDigitSystemFontOfSize:64 weight:UIFontWeightBold],NSForegroundColorAttributeName:UIColor.whiteColor,NSKernAttributeName:@4}];
  [items insertObject:c atIndex:2];
 }
 UIStackView *st=[[UIStackView alloc] initWithArrangedSubviews:items];
 st.axis=UILayoutConstraintAxisVertical;st.spacing=12;st.alignment=UIStackViewAlignmentLeading;st.translatesAutoresizingMaskIntoConstraints=NO;
 [card.contentView addSubview:st];
 [NSLayoutConstraint activateConstraints:@[
  [card.centerXAnchor constraintEqualToAnchor:self.centerXAnchor],[card.centerYAnchor constraintEqualToAnchor:self.centerYAnchor],
  [card.widthAnchor constraintEqualToConstant:500],
  [st.topAnchor constraintEqualToAnchor:card.contentView.topAnchor constant:28],[st.bottomAnchor constraintEqualToAnchor:card.contentView.bottomAnchor constant:-28],
  [st.leadingAnchor constraintEqualToAnchor:card.contentView.leadingAnchor constant:32],[st.trailingAnchor constraintEqualToAnchor:card.contentView.trailingAnchor constant:-32]]];
 [self addGestureRecognizer:[[UITapGestureRecognizer alloc] initWithTarget:self action:@selector(hide)]];
 return self;
}
- (void)showIn:(UIView*)parent autoHide:(NSTimeInterval)delay {
 self.frame=parent.bounds;self.autoresizingMask=UIViewAutoresizingFlexibleWidth|UIViewAutoresizingFlexibleHeight;
 self.alpha=0;[parent addSubview:self];
 [UIView animateWithDuration:.3 animations:^{self.alpha=1;}];
 if(delay>0){__weak typeof(self) w=self;dispatch_after(dispatch_time(DISPATCH_TIME_NOW,(int64_t)(delay*NSEC_PER_SEC)),dispatch_get_main_queue(),^{[w hide];});}
}
- (void)hide {
 [UIView animateWithDuration:.25 animations:^{self.alpha=0;} completion:^(BOOL done){(void)done;[self removeFromSuperview];}];
}
@end

// Liste des raccourcis : envoyée par le PC (« SC libellé|touches … »), sinon liste par défaut
static NSArray<NSArray<NSString*>*> *MPShortcuts(void){
 NSArray *saved=[NSUserDefaults.standardUserDefaults arrayForKey:@"shortcuts"];
 if(saved.count)return saved;
 return @[@[@"Copier",@"ctrl+c"],@[@"Coller",@"ctrl+v"],@[@"Couper",@"ctrl+x"],@[@"Annuler",@"ctrl+z"],@[@"Rétablir",@"ctrl+y"],
  @[@"Tout sélectionner",@"ctrl+a"],@[@"Enregistrer",@"ctrl+s"],@[@"Rechercher",@"ctrl+f"],@[@"Bureau",@"win+d"],@[@"Vue des tâches",@"win+tab"],
  @[@"Capture d'écran",@"win+shift+s"],@[@"Explorateur",@"win+e"],@[@"Verrouiller",@"win+l"],@[@"Fermer la fenêtre",@"alt+f4"],@[@"Plein écran",@"f11"],
  @[@"Micro Teams",@"ctrl+shift+m"],@[@"Volume −",@"voldown"],@[@"Volume +",@"volup"],@[@"Muet",@"mute"],@[@"Lecture / Pause",@"playpause"]];
}
static CAGradientLayer *MPBackdrop(void){
 CAGradientLayer *g=[CAGradientLayer layer];
 g.colors=@[(id)MPRGB(0x0A1438,1).CGColor,(id)MPRGB(0x1B1E6E,1).CGColor,(id)MPRGB(0x3A1670,1).CGColor];
 g.startPoint=CGPointMake(0,0);g.endPoint=CGPointMake(1,1);
 return g;
}
static UIButton *MPGlassButton(NSString *title,CGFloat size){
 UIButton *b=[UIButton buttonWithType:UIButtonTypeSystem];
 [b setTitle:title forState:UIControlStateNormal];[b setTitleColor:UIColor.whiteColor forState:UIControlStateNormal];
 b.titleLabel.font=[UIFont systemFontOfSize:size weight:UIFontWeightSemibold];
 b.titleLabel.numberOfLines=2;b.titleLabel.textAlignment=NSTextAlignmentCenter;
 b.backgroundColor=[UIColor colorWithWhite:1 alpha:.13];b.layer.cornerRadius=18;
 b.layer.borderWidth=1;b.layer.borderColor=[UIColor colorWithWhite:1 alpha:.28].CGColor;
 b.contentEdgeInsets=UIEdgeInsetsMake(8,14,8,14);
 return b;
}

// Pavé tactile plein écran (déplacement relatif, comme un PC portable)
@interface MPTrackpadView : UIView
@property(nonatomic,copy) void (^emit)(NSString *line);
@property(nonatomic,copy) void (^onKeyboard)(void);
- (void)showIn:(UIView*)parent;
@end
@implementation MPTrackpadView {
 CAGradientLayer *_bg;
 UIView *_pad;
 NSUInteger _fingers;BOOL _tap;
 NSTimeInterval _t0,_lastT;
 CGPoint _last,_start,_c0,_c3;
 CGFloat _fx,_fy,_scrollRef,_scrollAcc,_scrollTotal;
}
- (instancetype)initWithFrame:(CGRect)f {
 if(!(self=[super initWithFrame:f]))return nil;
 self.multipleTouchEnabled=YES;
 _bg=MPBackdrop();[self.layer addSublayer:_bg];
 _pad=[UIView new];_pad.translatesAutoresizingMaskIntoConstraints=NO;_pad.userInteractionEnabled=NO;
 _pad.backgroundColor=[UIColor colorWithWhite:1 alpha:.05];_pad.layer.cornerRadius=34;
 _pad.layer.borderWidth=1;_pad.layer.borderColor=[UIColor colorWithWhite:1 alpha:.14].CGColor;
 [self addSubview:_pad];
 UILabel *title=MPLabel(@"PAVÉ TACTILE",13,UIFontWeightSemibold,.7,3);title.translatesAutoresizingMaskIntoConstraints=NO;title.userInteractionEnabled=NO;
 UILabel *hint=MPLabel(@"1 doigt : déplacer · tap : clic · 2 doigts : clic droit / défiler · 3 doigts ↑ vue des tâches · ↓ bureau · ← → changer d'appli",14,UIFontWeightRegular,.55,0);
 hint.textAlignment=NSTextAlignmentCenter;hint.translatesAutoresizingMaskIntoConstraints=NO;hint.userInteractionEnabled=NO;
 [self addSubview:title];[self addSubview:hint];
 UIButton *left=MPGlassButton(@"Clic gauche\n(maintenir = glisser)",15),*right=MPGlassButton(@"Clic droit",16),*kb=MPGlassButton(@"Clavier",16),*close=MPGlassButton(@"Fermer",16);
 [left addTarget:self action:@selector(leftDown) forControlEvents:UIControlEventTouchDown];
 [left addTarget:self action:@selector(leftUp) forControlEvents:UIControlEventTouchUpInside|UIControlEventTouchUpOutside|UIControlEventTouchCancel];
 [right addTarget:self action:@selector(rightClick) forControlEvents:UIControlEventTouchUpInside];
 [kb addTarget:self action:@selector(keyboard) forControlEvents:UIControlEventTouchUpInside];
 [close addTarget:self action:@selector(hide) forControlEvents:UIControlEventTouchUpInside];
 UIStackView *bar=[[UIStackView alloc] initWithArrangedSubviews:@[left,right,kb,close]];
 bar.distribution=UIStackViewDistributionFillEqually;bar.spacing=12;bar.translatesAutoresizingMaskIntoConstraints=NO;
 [self addSubview:bar];
 [NSLayoutConstraint activateConstraints:@[
  [title.topAnchor constraintEqualToAnchor:self.topAnchor constant:26],[title.leadingAnchor constraintEqualToAnchor:self.leadingAnchor constant:32],
  [_pad.topAnchor constraintEqualToAnchor:title.bottomAnchor constant:14],[_pad.leadingAnchor constraintEqualToAnchor:self.leadingAnchor constant:24],
  [_pad.trailingAnchor constraintEqualToAnchor:self.trailingAnchor constant:-24],[_pad.bottomAnchor constraintEqualToAnchor:hint.topAnchor constant:-12],
  [hint.leadingAnchor constraintEqualToAnchor:self.leadingAnchor constant:24],[hint.trailingAnchor constraintEqualToAnchor:self.trailingAnchor constant:-24],
  [hint.bottomAnchor constraintEqualToAnchor:bar.topAnchor constant:-14],
  [bar.leadingAnchor constraintEqualToAnchor:self.leadingAnchor constant:24],[bar.trailingAnchor constraintEqualToAnchor:self.trailingAnchor constant:-24],
  [bar.bottomAnchor constraintEqualToAnchor:self.bottomAnchor constant:-22],[bar.heightAnchor constraintEqualToConstant:64]]];
 return self;
}
- (void)layoutSubviews {[super layoutSubviews];[CATransaction begin];[CATransaction setDisableActions:YES];_bg.frame=self.bounds;[CATransaction commit];}
- (void)out:(NSString*)l {if(self.emit)self.emit(l);}
- (void)leftDown {[self out:@"DD\n"];}
- (void)leftUp {[self out:@"DU\n"];}
- (void)rightClick {[self out:@"CR\n"];}
- (void)keyboard {if(self.onKeyboard)self.onKeyboard();}
- (NSArray<UITouch*>*)active:(UIEvent*)e {
 NSMutableArray *a=[NSMutableArray array];
 for(UITouch *t in [e touchesForView:self])if(t.phase!=UITouchPhaseEnded&&t.phase!=UITouchPhaseCancelled)[a addObject:t];
 return a;
}
- (CGPoint)centroid:(NSArray<UITouch*>*)ts {CGFloat x=0,y=0;for(UITouch *t in ts){CGPoint p=[t locationInView:self];x+=p.x;y+=p.y;}return CGPointMake(x/ts.count,y/ts.count);}
- (void)touchesBegan:(NSSet<UITouch*>*)touches withEvent:(UIEvent*)e {
 (void)touches;NSArray *a=[self active:e];
 if(_fingers==0){_tap=YES;_t0=e.timestamp;_start=[a.firstObject locationInView:self];}
 _fingers=MAX(_fingers,a.count);
 _last=[a.firstObject locationInView:self];_lastT=e.timestamp;
 CGPoint c=[self centroid:a];_scrollRef=c.y;_scrollAcc=0;_scrollTotal=0;
 if(a.count>=3)_c0=_c3=c;
}
- (void)touchesMoved:(NSSet<UITouch*>*)touches withEvent:(UIEvent*)e {
 (void)touches;NSArray *a=[self active:e];
 NSTimeInterval dt=fmax(e.timestamp-_lastT,.001);_lastT=e.timestamp;
 if(a.count==1&&_fingers==1){
  CGPoint p=[a[0] locationInView:self];CGFloat dx=p.x-_last.x,dy=p.y-_last.y;_last=p;
  CGFloat gain=1.4+fmin(hypot(dx,dy)/dt/600,2.2);   // accélération : plus le doigt va vite, plus le curseur avance
  _fx+=dx*gain;_fy+=dy*gain;int ix=(int)_fx,iy=(int)_fy;
  if(ix||iy){_fx-=ix;_fy-=iy;[self out:[NSString stringWithFormat:@"RM %d %d\n",ix,iy]];}
  if(hypot(p.x-_start.x,p.y-_start.y)>6)_tap=NO;
 } else if(a.count==2){
  CGPoint c=[self centroid:a];CGFloat dy=c.y-_scrollRef;_scrollRef=c.y;_scrollAcc+=dy;_scrollTotal+=fabs(dy);
  int units=(int)(_scrollAcc*MPScrollGain);
  if(units){_scrollAcc-=units/MPScrollGain;[self out:[NSString stringWithFormat:@"W %d\n",units]];}
  if(_scrollTotal>6)_tap=NO;
 } else if(a.count>=3){
  _c3=[self centroid:a];
  if(hypot(_c3.x-_c0.x,_c3.y-_c0.y)>15)_tap=NO;
 }
}
- (void)finish:(UIEvent*)e {
 if([self active:e].count>0)return;
 if(_fingers>=3){
  CGFloat dx=_c3.x-_c0.x,dy=_c3.y-_c0.y;
  if(fmax(fabs(dx),fabs(dy))>60){
   NSString *k=fabs(dy)>fabs(dx)?(dy<0?@"win%2Btab":@"win%2Bd"):@"alt%2Btab";
   [self out:[NSString stringWithFormat:@"KS %@\n",k]];
  }
 } else if(_tap&&e.timestamp-_t0<.3){
  [self out:_fingers==1?@"CL\n":@"CR\n"];
 }
 _fingers=0;
}
- (void)touchesEnded:(NSSet<UITouch*>*)touches withEvent:(UIEvent*)e {(void)touches;[self finish:e];}
- (void)touchesCancelled:(NSSet<UITouch*>*)touches withEvent:(UIEvent*)e {(void)touches;_tap=NO;[self finish:e];}
- (void)showIn:(UIView*)parent {
 self.frame=parent.bounds;self.autoresizingMask=UIViewAutoresizingFlexibleWidth|UIViewAutoresizingFlexibleHeight;
 self.alpha=0;[parent addSubview:self];[UIView animateWithDuration:.3 animations:^{self.alpha=1;}];
}
- (void)hide {[self leftUp];[UIView animateWithDuration:.25 animations:^{self.alpha=0;} completion:^(BOOL done){(void)done;[self removeFromSuperview];}];}
@end

// Grille de raccourcis façon « Stream Deck »
@interface MPShortcutsView : UIView
@property(nonatomic,copy) void (^emit)(NSString *line);
- (void)showIn:(UIView*)parent;
@end
@implementation MPShortcutsView { CAGradientLayer *_bg; NSArray *_items; }
- (instancetype)initWithFrame:(CGRect)f {
 if(!(self=[super initWithFrame:f]))return nil;
 _bg=MPBackdrop();[self.layer addSublayer:_bg];
 _items=MPShortcuts();
 UILabel *title=MPLabel(@"Raccourcis",30,UIFontWeightBold,1,-.4);
 UIButton *close=MPGlassButton(@"Fermer",16);
 [close addTarget:self action:@selector(hide) forControlEvents:UIControlEventTouchUpInside];
 UIStackView *head=[[UIStackView alloc] initWithArrangedSubviews:@[title,close]];
 head.distribution=UIStackViewDistributionEqualSpacing;head.alignment=UIStackViewAlignmentCenter;head.translatesAutoresizingMaskIntoConstraints=NO;
 [self addSubview:head];
 UIStackView *grid=[UIStackView new];grid.axis=UILayoutConstraintAxisVertical;grid.spacing=14;grid.distribution=UIStackViewDistributionFillEqually;
 grid.translatesAutoresizingMaskIntoConstraints=NO;[self addSubview:grid];
 NSUInteger cols=5;UIStackView *row=nil;
 for(NSUInteger i=0;i<_items.count;i++){
  if(i%cols==0){row=[UIStackView new];row.spacing=14;row.distribution=UIStackViewDistributionFillEqually;[grid addArrangedSubview:row];}
  UIButton *b=MPGlassButton(_items[i][0],18);b.tag=(NSInteger)i;
  [b addTarget:self action:@selector(press:) forControlEvents:UIControlEventTouchUpInside];
  [row addArrangedSubview:b];
 }
 while(row&&row.arrangedSubviews.count<cols){UIView *v=[UIView new];[row addArrangedSubview:v];}   // garde des cases de même taille
 UILabel *note=MPLabel(@"Liste modifiable sur le PC : %APPDATA%\\MirrorPad\\config.json, section « Shortcuts ».",13,UIFontWeightRegular,.5,0);
 note.translatesAutoresizingMaskIntoConstraints=NO;[self addSubview:note];
 [NSLayoutConstraint activateConstraints:@[
  [head.topAnchor constraintEqualToAnchor:self.topAnchor constant:24],[head.leadingAnchor constraintEqualToAnchor:self.leadingAnchor constant:28],
  [head.trailingAnchor constraintEqualToAnchor:self.trailingAnchor constant:-28],
  [grid.topAnchor constraintEqualToAnchor:head.bottomAnchor constant:20],[grid.leadingAnchor constraintEqualToAnchor:self.leadingAnchor constant:28],
  [grid.trailingAnchor constraintEqualToAnchor:self.trailingAnchor constant:-28],[grid.bottomAnchor constraintEqualToAnchor:note.topAnchor constant:-14],
  [note.leadingAnchor constraintEqualToAnchor:self.leadingAnchor constant:28],[note.bottomAnchor constraintEqualToAnchor:self.bottomAnchor constant:-18]]];
 return self;
}
- (void)layoutSubviews {[super layoutSubviews];[CATransaction begin];[CATransaction setDisableActions:YES];_bg.frame=self.bounds;[CATransaction commit];}
- (void)press:(UIButton*)b {
 NSString *combo=_items[(NSUInteger)b.tag][1];
 if(self.emit)self.emit([NSString stringWithFormat:@"KS %@\n",MPEsc(combo)]);
 [UIView animateWithDuration:.08 animations:^{b.transform=CGAffineTransformMakeScale(.94,.94);b.backgroundColor=[UIColor colorWithWhite:1 alpha:.32];}
  completion:^(BOOL d){(void)d;[UIView animateWithDuration:.18 animations:^{b.transform=CGAffineTransformIdentity;b.backgroundColor=[UIColor colorWithWhite:1 alpha:.13];}];}];
}
- (void)showIn:(UIView*)parent {
 self.frame=parent.bounds;self.autoresizingMask=UIViewAutoresizingFlexibleWidth|UIViewAutoresizingFlexibleHeight;
 self.alpha=0;[parent addSubview:self];[UIView animateWithDuration:.3 animations:^{self.alpha=1;}];
}
- (void)hide {[UIView animateWithDuration:.25 animations:^{self.alpha=0;} completion:^(BOOL done){(void)done;[self removeFromSuperview];}];}
@end

// Touch Bar contextuelle, façon MacBook : bande noire collée au bas de l'écran. L'image du PC s'arrête juste
// au-dessus (la barre des tâches Windows reste visible). Repliable : il ne reste qu'une petite languette.
static const CGFloat MPTouchBarHeight=50;
@interface MPTouchBar : UIView
@property(nonatomic,copy) void (^onAction)(NSString *keys);   // « ctrl+s », « f5 », « @swap », « @numpad », « @keyboard »…
@property(nonatomic,copy) void (^onFold)(void);               // repliée / dépliée : l'image doit être recalculée
@property(nonatomic) BOOL collapsed;
- (void)setTitle:(NSString*)title items:(NSArray<NSArray<NSString*>*>*)items;
@end
@implementation MPTouchBar { UIView *_strip; CAGradientLayer *_line; UILabel *_title; UIScrollView *_scroll; UIStackView *_stack; UIButton *_fold,*_tab; NSArray *_items; }
- (instancetype)initWithFrame:(CGRect)f {
 if(!(self=[super initWithFrame:f]))return nil;
 _strip=[UIView new];_strip.backgroundColor=[UIColor colorWithWhite:.04 alpha:1];[self addSubview:_strip];
 _line=[CAGradientLayer layer];   // fin filet aux couleurs de l'app sur le bord supérieur
 _line.colors=@[(id)MPRGB(0x16C6FF,.9).CGColor,(id)MPRGB(0x3B5BFF,.9).CGColor,(id)MPRGB(0x7A2EF6,.9).CGColor];
 _line.startPoint=CGPointMake(0,.5);_line.endPoint=CGPointMake(1,.5);[_strip.layer addSublayer:_line];
 _title=MPLabel(@"",11,UIFontWeightBold,.5,.8);_title.textAlignment=NSTextAlignmentLeft;[_strip addSubview:_title];
 _scroll=[UIScrollView new];_scroll.showsHorizontalScrollIndicator=NO;_scroll.alwaysBounceHorizontal=YES;[_strip addSubview:_scroll];
 _stack=[UIStackView new];_stack.spacing=6;[_scroll addSubview:_stack];
 _fold=[UIButton buttonWithType:UIButtonTypeSystem];[_fold setTitle:@"⌄" forState:UIControlStateNormal];
 _fold.titleLabel.font=[UIFont systemFontOfSize:22 weight:UIFontWeightBold];[_fold setTitleColor:[UIColor colorWithWhite:1 alpha:.7] forState:UIControlStateNormal];
 [_fold addTarget:self action:@selector(toggle) forControlEvents:UIControlEventTouchUpInside];[_strip addSubview:_fold];
 // languette visible quand la barre est repliée (coin bas droit)
 _tab=[UIButton buttonWithType:UIButtonTypeSystem];[_tab setTitle:@"⌃" forState:UIControlStateNormal];
 _tab.titleLabel.font=[UIFont systemFontOfSize:18 weight:UIFontWeightBold];[_tab setTitleColor:UIColor.whiteColor forState:UIControlStateNormal];
 _tab.backgroundColor=[UIColor colorWithWhite:.04 alpha:.85];_tab.layer.cornerRadius=10;_tab.layer.borderWidth=1;_tab.layer.borderColor=MPRGB(0x3B5BFF,.8).CGColor;
 _tab.hidden=YES;[_tab addTarget:self action:@selector(toggle) forControlEvents:UIControlEventTouchUpInside];[self addSubview:_tab];
 return self;
}
- (UIView*)hitTest:(CGPoint)p withEvent:(UIEvent*)e {UIView *v=[super hitTest:p withEvent:e];return v==self?nil:v;}   // hors barre / languette : le toucher passe au PC
- (void)toggle {
 self.collapsed=!self.collapsed;[MPDefaults() setBool:self.collapsed forKey:@"touchBarFolded"];
 if(self.onFold)self.onFold();
}
- (void)setCollapsed:(BOOL)c {
 _collapsed=c;
 [UIView animateWithDuration:.25 animations:^{[self layoutStrip];}];
}
- (void)layoutStrip {
 CGFloat w=self.bounds.size.width,h=self.bounds.size.height;
 _strip.frame=CGRectMake(0,_collapsed?h:0,w,h);
 _tab.hidden=!_collapsed;_tab.frame=CGRectMake(w-54,h-30,46,26);
}
- (void)layoutSubviews {
 [super layoutSubviews];
 [self layoutStrip];
 CGFloat w=self.bounds.size.width,h=self.bounds.size.height;
 _line.frame=CGRectMake(0,0,w,1.5);
 _title.frame=CGRectMake(14,0,96,h);
 _fold.frame=CGRectMake(w-46,0,44,h);
 _scroll.frame=CGRectMake(112,7,w-112-50,h-12);
 CGSize sz=[_stack systemLayoutSizeFittingSize:UILayoutFittingCompressedSize];
 _stack.frame=CGRectMake(0,0,fmax(sz.width,1),_scroll.bounds.size.height);
 _scroll.contentSize=_stack.frame.size;
}
- (void)setTitle:(NSString*)title items:(NSArray<NSArray<NSString*>*>*)items {
 _title.text=title.uppercaseString;_items=items;
 for(UIView *v in _stack.arrangedSubviews){[_stack removeArrangedSubview:v];[v removeFromSuperview];}
 for(NSUInteger i=0;i<items.count;i++){
  // touches façon Touch Bar : gris anthracite arrondi, actions MirrorPad en bleu
  UIButton *b=[UIButton buttonWithType:UIButtonTypeCustom];b.tag=(NSInteger)i;
  [b setTitle:items[i][0] forState:UIControlStateNormal];[b setTitleColor:UIColor.whiteColor forState:UIControlStateNormal];
  b.titleLabel.font=[UIFont systemFontOfSize:15 weight:UIFontWeightMedium];
  b.contentEdgeInsets=UIEdgeInsetsMake(0,16,0,16);b.layer.cornerRadius=8;
  b.backgroundColor=[items[i][1] hasPrefix:@"@"]?MPRGB(0x3B5BFF,.85):[UIColor colorWithWhite:.22 alpha:1];
  [b addTarget:self action:@selector(press:) forControlEvents:UIControlEventTouchUpInside];
  [_stack addArrangedSubview:b];
 }
 [self setNeedsLayout];
 _scroll.contentOffset=CGPointZero;
 _title.alpha=.1;[UIView animateWithDuration:.4 animations:^{self->_title.alpha=.5;}];
}
- (void)press:(UIButton*)b {
 if((NSUInteger)b.tag>=_items.count)return;
 if(self.onAction)self.onAction(_items[(NSUInteger)b.tag][1]);
 UIColor *bg=b.backgroundColor;
 [UIView animateWithDuration:.06 animations:^{b.backgroundColor=[UIColor colorWithWhite:.45 alpha:1];}
  completion:^(BOOL d){(void)d;[UIView animateWithDuration:.2 animations:^{b.backgroundColor=bg;}];}];
}
@end

// Pavé numérique + calculatrice : les touches partent vers le PC (Excel…), le calcul local s'affiche en haut,
// « Entrée » archive la ligne dans l'historique ; toucher un résultat l'envoie au PC
@interface MPNumpadView : UIView
@property(nonatomic,copy) void (^emit)(NSString *line);
- (void)showIn:(UIView*)parent;
@end
@implementation MPNumpadView { CAGradientLayer *_bg; UILabel *_expr,*_result; UIStackView *_history; NSMutableString *_line; }
- (instancetype)initWithFrame:(CGRect)f {
 if(!(self=[super initWithFrame:f]))return nil;
 _bg=MPBackdrop();[self.layer addSublayer:_bg];_line=[NSMutableString string];
 UILabel *title=MPLabel(@"Pavé numérique",30,UIFontWeightBold,1,-.4);
 UIButton *close=MPGlassButton(@"Fermer",16);[close addTarget:self action:@selector(hide) forControlEvents:UIControlEventTouchUpInside];
 UIStackView *head=[[UIStackView alloc] initWithArrangedSubviews:@[title,close]];head.distribution=UIStackViewDistributionEqualSpacing;head.alignment=UIStackViewAlignmentCenter;
 _expr=MPLabel(@" ",28,UIFontWeightMedium,.9,0);_expr.textAlignment=NSTextAlignmentRight;
 _result=MPLabel(@" ",20,UIFontWeightRegular,.6,0);_result.textAlignment=NSTextAlignmentRight;
 NSArray *rows=@[@[@"C",@"÷",@"×",@"⌫"],@[@"7",@"8",@"9",@"−"],@[@"4",@"5",@"6",@"+"],@[@"1",@"2",@"3",@"Tab"],@[@"0",@".",@"=",@"Entrée"]];
 UIStackView *grid=[UIStackView new];grid.axis=UILayoutConstraintAxisVertical;grid.spacing=10;grid.distribution=UIStackViewDistributionFillEqually;
 for(NSArray *r in rows){
  UIStackView *row=[UIStackView new];row.spacing=10;row.distribution=UIStackViewDistributionFillEqually;
  for(NSString *k in r){UIButton *b=MPGlassButton(k,26);[b setTitle:k forState:UIControlStateNormal];
   if([@"Entrée=" rangeOfString:k].location!=NSNotFound)b.backgroundColor=MPRGB(0x3B5BFF,.5);
   [b addTarget:self action:@selector(key:) forControlEvents:UIControlEventTouchUpInside];[row addArrangedSubview:b];}
  [grid addArrangedSubview:row];
 }
 UILabel *hTitle=MPLabel(@"HISTORIQUE (toucher = envoyer au PC)",12,UIFontWeightSemibold,.55,.4);
 _history=[UIStackView new];_history.axis=UILayoutConstraintAxisVertical;_history.spacing=6;
 UIStackView *right=[[UIStackView alloc] initWithArrangedSubviews:@[_expr,_result,hTitle,_history,[UIView new]]];right.axis=UILayoutConstraintAxisVertical;right.spacing=10;
 UIStackView *body=[[UIStackView alloc] initWithArrangedSubviews:@[grid,right]];body.spacing=24;body.distribution=UIStackViewDistributionFillEqually;
 UIStackView *all=[[UIStackView alloc] initWithArrangedSubviews:@[head,body]];all.axis=UILayoutConstraintAxisVertical;all.spacing=20;
 all.translatesAutoresizingMaskIntoConstraints=NO;[self addSubview:all];
 [NSLayoutConstraint activateConstraints:@[[all.topAnchor constraintEqualToAnchor:self.topAnchor constant:24],[all.bottomAnchor constraintEqualToAnchor:self.bottomAnchor constant:-24],
  [all.leadingAnchor constraintEqualToAnchor:self.leadingAnchor constant:28],[all.trailingAnchor constraintEqualToAnchor:self.trailingAnchor constant:-28]]];
 return self;
}
- (void)layoutSubviews {[super layoutSubviews];[CATransaction begin];[CATransaction setDisableActions:YES];_bg.frame=self.bounds;[CATransaction commit];}
- (void)send:(NSString*)combo {if(self.emit)self.emit([NSString stringWithFormat:@"KS %@\n",combo]);}
- (NSString*)evaluate {
 NSString *e=[[[_line stringByReplacingOccurrencesOfString:@"×" withString:@"*"] stringByReplacingOccurrencesOfString:@"÷" withString:@"/"] stringByReplacingOccurrencesOfString:@"−" withString:@"-"];
 if(!e.length)return nil;
 unichar last=[e characterAtIndex:e.length-1];if(last=='+'||last=='-'||last=='*'||last=='/'||last=='.')return nil;
 @try {
  // nombres entiers -> décimaux, sinon NSExpression ferait une division entière (1/2 = 0)
  NSRegularExpression *ints=[NSRegularExpression regularExpressionWithPattern:@"(?<![0-9.])([0-9]+)(?![0-9.])" options:0 error:nil];
  e=[ints stringByReplacingMatchesInString:e options:0 range:NSMakeRange(0,e.length) withTemplate:@"$1.0"];
  NSExpression *x=[NSExpression expressionWithFormat:e];
  NSNumber *n=[x expressionValueWithObject:nil context:nil];
  if(!n||isnan(n.doubleValue)||isinf(n.doubleValue))return nil;
  NSNumberFormatter *f=[NSNumberFormatter new];f.maximumFractionDigits=8;f.minimumIntegerDigits=1;f.decimalSeparator=@".";f.usesGroupingSeparator=NO;
  return [f stringFromNumber:n];
 } @catch(NSException *ex){return nil;}
}
- (void)refresh {_expr.text=_line.length?_line:@" ";NSString *r=[self evaluate];_result.text=r?[@"= " stringByAppendingString:r]:@" ";}
- (void)key:(UIButton*)b {
 NSString *k=[b titleForState:UIControlStateNormal];
 static NSDictionary *map;
 if(!map)map=@{@"0":@"num0",@"1":@"num1",@"2":@"num2",@"3":@"num3",@"4":@"num4",@"5":@"num5",@"6":@"num6",@"7":@"num7",@"8":@"num8",@"9":@"num9",
               @"+":@"numadd",@"−":@"numsub",@"×":@"nummul",@"÷":@"numdiv",@".":@"numdec",@"Tab":@"tab",@"⌫":@"back"};
 if([k isEqualToString:@"C"]){[_line setString:@""];}
 else if([k isEqualToString:@"="]){NSString *r=[self evaluate];if(r)[self archive:r];}
 else if([k isEqualToString:@"Entrée"]){[self send:@"enter"];NSString *r=[self evaluate];if(r)[self archive:r];else [_line setString:@""];}
 else {
  if(map[k])[self send:map[k]];
  if([k isEqualToString:@"⌫"]){if(_line.length)[_line deleteCharactersInRange:NSMakeRange(_line.length-1,1)];}
  else if([k isEqualToString:@"Tab"])[_line setString:@""];
  else [_line appendString:k];
 }
 [self refresh];
}
- (void)archive:(NSString*)r {
 UIButton *h=[UIButton buttonWithType:UIButtonTypeSystem];
 [h setTitle:[NSString stringWithFormat:@"%@ = %@",_line,r] forState:UIControlStateNormal];h.accessibilityValue=r;
 h.titleLabel.font=[UIFont monospacedDigitSystemFontOfSize:18 weight:UIFontWeightMedium];[h setTitleColor:UIColor.whiteColor forState:UIControlStateNormal];
 h.contentHorizontalAlignment=UIControlContentHorizontalAlignmentRight;
 [h addTarget:self action:@selector(sendResult:) forControlEvents:UIControlEventTouchUpInside];
 [_history insertArrangedSubview:h atIndex:0];
 while(_history.arrangedSubviews.count>8){UIView *v=_history.arrangedSubviews.lastObject;[_history removeArrangedSubview:v];[v removeFromSuperview];}
 [_line setString:@""];
}
- (void)sendResult:(UIButton*)b {if(self.emit)self.emit([NSString stringWithFormat:@"T %@\n",MPEsc(b.accessibilityValue)]);}
- (void)showIn:(UIView*)parent {
 self.frame=parent.bounds;self.autoresizingMask=UIViewAutoresizingFlexibleWidth|UIViewAutoresizingFlexibleHeight;
 self.alpha=0;[parent addSubview:self];[UIView animateWithDuration:.3 animations:^{self.alpha=1;}];
}
- (void)hide {[UIView animateWithDuration:.25 animations:^{self.alpha=0;} completion:^(BOOL done){(void)done;[self removeFromSuperview];}];}
@end

// Tableau de bord : horloge + état du PC en direct (processeur, mémoire, réseau, disque) + contrôles personnalisés
@interface MPDashboardView : UIView
@property(nonatomic,copy) void (^onClose)(void);
- (void)showIn:(UIView*)parent;
- (void)update:(NSArray<NSString*>*)p;
@end
@implementation MPDashboardView { CAGradientLayer *_bg; UILabel *_clock,*_date,*_pc; NSMutableDictionary<NSString*,UILabel*> *_values; NSMutableDictionary<NSString*,UIView*> *_bars; UIStackView *_checks; NSTimer *_tick; }
- (UIView*)card:(NSString*)key title:(NSString*)title {
 UIView *c=[UIView new];c.backgroundColor=[UIColor colorWithWhite:1 alpha:.10];c.layer.cornerRadius=18;c.layer.borderWidth=1;c.layer.borderColor=[UIColor colorWithWhite:1 alpha:.18].CGColor;
 UILabel *t=MPLabel(title.uppercaseString,12,UIFontWeightBold,.6,.8);t.textAlignment=NSTextAlignmentLeft;
 UILabel *v=MPLabel(@"—",30,UIFontWeightBold,1,-.5);v.textAlignment=NSTextAlignmentLeft;v.adjustsFontSizeToFitWidth=YES;
 UIView *track=[UIView new];track.backgroundColor=[UIColor colorWithWhite:1 alpha:.12];track.layer.cornerRadius=3;
 UIView *bar=[UIView new];bar.backgroundColor=MPRGB(0x16C6FF,1);bar.layer.cornerRadius=3;[track addSubview:bar];
 for(UIView *x in @[t,v,track]){x.translatesAutoresizingMaskIntoConstraints=NO;[c addSubview:x];}
 [NSLayoutConstraint activateConstraints:@[[t.topAnchor constraintEqualToAnchor:c.topAnchor constant:16],[t.leadingAnchor constraintEqualToAnchor:c.leadingAnchor constant:18],
  [v.topAnchor constraintEqualToAnchor:t.bottomAnchor constant:6],[v.leadingAnchor constraintEqualToAnchor:t.leadingAnchor],[v.trailingAnchor constraintEqualToAnchor:c.trailingAnchor constant:-18],
  [track.leadingAnchor constraintEqualToAnchor:t.leadingAnchor],[track.trailingAnchor constraintEqualToAnchor:c.trailingAnchor constant:-18],
  [track.bottomAnchor constraintEqualToAnchor:c.bottomAnchor constant:-18],[track.heightAnchor constraintEqualToConstant:6]]];
 _values[key]=v;_bars[key]=bar;bar.frame=CGRectMake(0,0,0,6);
 return c;
}
- (instancetype)initWithFrame:(CGRect)f {
 if(!(self=[super initWithFrame:f]))return nil;
 _bg=MPBackdrop();[self.layer addSublayer:_bg];
 _values=[NSMutableDictionary dictionary];_bars=[NSMutableDictionary dictionary];
 _clock=MPLabel(@"",64,UIFontWeightThin,1,-1);_clock.textAlignment=NSTextAlignmentLeft;
 _date=MPLabel(@"",18,UIFontWeightMedium,.7,0);_date.textAlignment=NSTextAlignmentLeft;
 _pc=MPLabel(@"",14,UIFontWeightSemibold,.55,.6);_pc.textAlignment=NSTextAlignmentLeft;
 UIButton *close=MPGlassButton(@"Fermer",16);[close addTarget:self action:@selector(hide) forControlEvents:UIControlEventTouchUpInside];
 UIStackView *timeCol=[[UIStackView alloc] initWithArrangedSubviews:@[_clock,_date,_pc]];timeCol.axis=UILayoutConstraintAxisVertical;timeCol.spacing=2;
 UIStackView *head=[[UIStackView alloc] initWithArrangedSubviews:@[timeCol,close]];head.alignment=UIStackViewAlignmentTop;head.distribution=UIStackViewDistributionEqualSpacing;
 UIStackView *r1=[[UIStackView alloc] initWithArrangedSubviews:@[[self card:@"cpu" title:@"Processeur"],[self card:@"ram" title:@"Mémoire"],[self card:@"disk" title:@"Disque système"]]];
 UIStackView *r2=[[UIStackView alloc] initWithArrangedSubviews:@[[self card:@"down" title:@"Réception"],[self card:@"up" title:@"Envoi"],[self card:@"uptime" title:@"Allumé depuis"]]];
 for(UIStackView *r in @[r1,r2]){r.spacing=14;r.distribution=UIStackViewDistributionFillEqually;}
 UILabel *ct=MPLabel(@"SURVEILLANCE",12,UIFontWeightBold,.6,.8);ct.textAlignment=NSTextAlignmentLeft;
 _checks=[UIStackView new];_checks.axis=UILayoutConstraintAxisVertical;_checks.spacing=8;
 [_checks addArrangedSubview:MPLabel(@"Ajoutez vos sites et serveurs dans config.json du PC (section « DashboardChecks »).",14,UIFontWeightRegular,.55,0)];
 UIStackView *all=[[UIStackView alloc] initWithArrangedSubviews:@[head,r1,r2,ct,_checks,[UIView new]]];all.axis=UILayoutConstraintAxisVertical;all.spacing=16;
 [r1.heightAnchor constraintEqualToConstant:130].active=YES;[r2.heightAnchor constraintEqualToConstant:130].active=YES;
 all.translatesAutoresizingMaskIntoConstraints=NO;[self addSubview:all];
 [NSLayoutConstraint activateConstraints:@[[all.topAnchor constraintEqualToAnchor:self.topAnchor constant:28],[all.bottomAnchor constraintEqualToAnchor:self.bottomAnchor constant:-20],
  [all.leadingAnchor constraintEqualToAnchor:self.leadingAnchor constant:28],[all.trailingAnchor constraintEqualToAnchor:self.trailingAnchor constant:-28]]];
 [self tickClock];
 __weak typeof(self) w=self;_tick=[NSTimer scheduledTimerWithTimeInterval:1 repeats:YES block:^(NSTimer *t){(void)t;[w tickClock];}];
 return self;
}
- (void)tickClock {
 NSDateFormatter *f=[NSDateFormatter new];f.locale=[NSLocale localeWithLocaleIdentifier:@"fr_FR"];
 f.dateFormat=@"HH:mm";_clock.text=[f stringFromDate:[NSDate date]];
 f.dateFormat=@"EEEE d MMMM";_date.text=[f stringFromDate:[NSDate date]].capitalizedString;
}
- (void)setBar:(NSString*)k fraction:(double)x warn:(BOOL)warn {
 UIView *b=_bars[k];UIView *track=b.superview;if(!b)return;
 x=fmin(1,fmax(0,x));
 [UIView animateWithDuration:.5 animations:^{b.frame=CGRectMake(0,0,track.bounds.size.width*x,6);}];
 b.backgroundColor=warn?MPRGB(0xFF6B6B,1):(x>.7?MPRGB(0xFFC14D,1):MPRGB(0x16C6FF,1));
}
static NSString *MPRate(double kb){return kb>=1024?[[NSString stringWithFormat:@"%.1f Mo/s",kb/1024] stringByReplacingOccurrencesOfString:@"." withString:@","]:[NSString stringWithFormat:@"%.0f Ko/s",kb];}
// « DB cpu ramUsed ramTotal upKo downKo disque% minutes nomPC » ; « DBX nom|ok|détail … »
- (void)update:(NSArray<NSString*>*)p {
 if([p[0] isEqualToString:@"DB"]&&p.count>=9){
  double cpu=p[1].doubleValue,used=p[2].doubleValue,total=p[3].doubleValue,up=p[4].doubleValue,down=p[5].doubleValue,disk=p[6].doubleValue;long mins=p[7].integerValue;
  _values[@"cpu"].text=[NSString stringWithFormat:@"%.0f %%",cpu];[self setBar:@"cpu" fraction:cpu/100 warn:cpu>90];
  _values[@"ram"].text=[[NSString stringWithFormat:@"%.1f / %.0f Go",used,total] stringByReplacingOccurrencesOfString:@"." withString:@","];[self setBar:@"ram" fraction:total>0?used/total:0 warn:total>0&&used/total>.92];
  _values[@"disk"].text=[NSString stringWithFormat:@"%.0f %% utilisé",disk];[self setBar:@"disk" fraction:disk/100 warn:disk>92];
  _values[@"down"].text=MPRate(down);[self setBar:@"down" fraction:down/(12.5*1024) warn:NO];   // échelle : 100 Mb/s
  _values[@"up"].text=MPRate(up);[self setBar:@"up" fraction:up/(12.5*1024) warn:NO];
  _values[@"uptime"].text=mins>=1440?[NSString stringWithFormat:@"%ld j %ld h",mins/1440,(mins%1440)/60]:[NSString stringWithFormat:@"%ld h %02ld",mins/60,mins%60];
  [self setBar:@"uptime" fraction:0 warn:NO];
  _pc.text=[[p[8] stringByRemovingPercentEncoding]?:@"" uppercaseString];
 } else if([p[0] isEqualToString:@"DBX"]){
  for(UIView *v in _checks.arrangedSubviews){[_checks removeArrangedSubview:v];[v removeFromSuperview];}
  for(NSUInteger i=1;i<p.count;i++){
   NSArray *f=[[p[i] stringByRemovingPercentEncoding] componentsSeparatedByString:@"|"];if(f.count<3)continue;
   BOOL ok=[f[1] isEqualToString:@"ok"];
   UILabel *l=MPLabel([NSString stringWithFormat:@"●  %@   —   %@",f[0],f[2]],17,UIFontWeightMedium,1,0);l.textAlignment=NSTextAlignmentLeft;
   NSMutableAttributedString *a=[[NSMutableAttributedString alloc] initWithString:l.text attributes:@{NSForegroundColorAttributeName:UIColor.whiteColor,NSFontAttributeName:l.font}];
   [a addAttribute:NSForegroundColorAttributeName value:ok?MPRGB(0x4CD964,1):MPRGB(0xFF6B6B,1) range:NSMakeRange(0,1)];l.attributedText=a;
   [_checks addArrangedSubview:l];
  }
 }
}
- (void)layoutSubviews {[super layoutSubviews];[CATransaction begin];[CATransaction setDisableActions:YES];_bg.frame=self.bounds;[CATransaction commit];}
- (void)showIn:(UIView*)parent {
 self.frame=parent.bounds;self.autoresizingMask=UIViewAutoresizingFlexibleWidth|UIViewAutoresizingFlexibleHeight;
 self.alpha=0;[parent addSubview:self];[UIView animateWithDuration:.3 animations:^{self.alpha=1;}];
}
- (void)hide {
 [_tick invalidate];_tick=nil;
 if(self.onClose)self.onClose();
 [UIView animateWithDuration:.25 animations:^{self.alpha=0;} completion:^(BOOL done){(void)done;[self removeFromSuperview];}];
}
@end

// Écran de la webcam : aperçu, changement de caméra ; « Masquer » laisse la webcam tourner
@interface MPCameraView : UIView
@property(nonatomic,copy) void (^onSwitch)(void);
@property(nonatomic,copy) void (^onStop)(void);
- (void)attach:(AVCaptureSession*)session;
- (void)showIn:(UIView*)parent;
- (void)hide;
@end
@implementation MPCameraView { AVCaptureVideoPreviewLayer *_preview; }
- (instancetype)initWithFrame:(CGRect)f {
 if(!(self=[super initWithFrame:f]))return nil;
 self.backgroundColor=UIColor.blackColor;
 UILabel *badge=MPLabel(@"●  WEBCAM ACTIVE  ·  « MirrorPad (iPad) » sur le PC",14,UIFontWeightSemibold,1,1);
 UIView *pill=[UIView new];pill.backgroundColor=[UIColor colorWithRed:.8 green:.1 blue:.2 alpha:.75];pill.layer.cornerRadius=18;
 pill.translatesAutoresizingMaskIntoConstraints=NO;badge.translatesAutoresizingMaskIntoConstraints=NO;
 [pill addSubview:badge];[self addSubview:pill];
 UIButton *sw=MPGlassButton(@"Changer de caméra",16),*hide=MPGlassButton(@"Masquer (la webcam continue)",16),*stop=MPGlassButton(@"Arrêter la webcam",16);
 stop.backgroundColor=[UIColor colorWithRed:.8 green:.1 blue:.2 alpha:.55];
 [sw addTarget:self action:@selector(switchCam) forControlEvents:UIControlEventTouchUpInside];
 [hide addTarget:self action:@selector(hide) forControlEvents:UIControlEventTouchUpInside];
 [stop addTarget:self action:@selector(stopCam) forControlEvents:UIControlEventTouchUpInside];
 UIStackView *bar=[[UIStackView alloc] initWithArrangedSubviews:@[sw,hide,stop]];
 bar.distribution=UIStackViewDistributionFillEqually;bar.spacing=12;bar.translatesAutoresizingMaskIntoConstraints=NO;
 [self addSubview:bar];
 [NSLayoutConstraint activateConstraints:@[
  [pill.topAnchor constraintEqualToAnchor:self.topAnchor constant:22],[pill.centerXAnchor constraintEqualToAnchor:self.centerXAnchor],
  [badge.topAnchor constraintEqualToAnchor:pill.topAnchor constant:9],[badge.bottomAnchor constraintEqualToAnchor:pill.bottomAnchor constant:-9],
  [badge.leadingAnchor constraintEqualToAnchor:pill.leadingAnchor constant:18],[badge.trailingAnchor constraintEqualToAnchor:pill.trailingAnchor constant:-18],
  [bar.leadingAnchor constraintEqualToAnchor:self.leadingAnchor constant:24],[bar.trailingAnchor constraintEqualToAnchor:self.trailingAnchor constant:-24],
  [bar.bottomAnchor constraintEqualToAnchor:self.bottomAnchor constant:-22],[bar.heightAnchor constraintEqualToConstant:58]]];
 return self;
}
- (void)attach:(AVCaptureSession*)session {
 [_preview removeFromSuperlayer];
 if(!session)return;
 _preview=[AVCaptureVideoPreviewLayer layerWithSession:session];
 _preview.videoGravity=AVLayerVideoGravityResizeAspectFill;
 [self.layer insertSublayer:_preview atIndex:0];
 [self setNeedsLayout];
}
- (void)layoutSubviews {
 [super layoutSubviews];
 [CATransaction begin];[CATransaction setDisableActions:YES];_preview.frame=self.bounds;[CATransaction commit];
 AVCaptureConnection *c=_preview.connection;
 if(c.isVideoOrientationSupported){
  UIInterfaceOrientation o=UIApplication.sharedApplication.statusBarOrientation;
  c.videoOrientation=o==UIInterfaceOrientationLandscapeLeft?AVCaptureVideoOrientationLandscapeLeft:
   o==UIInterfaceOrientationLandscapeRight?AVCaptureVideoOrientationLandscapeRight:
   o==UIInterfaceOrientationPortraitUpsideDown?AVCaptureVideoOrientationPortraitUpsideDown:AVCaptureVideoOrientationPortrait;
 }
}
- (void)switchCam {if(self.onSwitch)self.onSwitch();}
- (void)stopCam {if(self.onStop)self.onStop();[self hide];}
- (void)showIn:(UIView*)parent {
 self.frame=parent.bounds;self.autoresizingMask=UIViewAutoresizingFlexibleWidth|UIViewAutoresizingFlexibleHeight;
 self.alpha=0;[parent addSubview:self];[UIView animateWithDuration:.3 animations:^{self.alpha=1;}];
}
- (void)hide {[UIView animateWithDuration:.25 animations:^{self.alpha=0;} completion:^(BOOL done){(void)done;[self removeFromSuperview];}];}
@end

// Petit bandeau en haut à droite : images/s, débit, définition, encodeur, latence réseau
@interface MPHud : UIView
@property(nonatomic,readonly) UILabel *label;
@end
@implementation MPHud
- (instancetype)initWithFrame:(CGRect)f {
 if(!(self=[super initWithFrame:f]))return nil;
 self.backgroundColor=[UIColor colorWithRed:.03 green:.04 blue:.14 alpha:.72];
 self.layer.cornerRadius=14;self.layer.borderWidth=1;self.layer.borderColor=[UIColor colorWithWhite:1 alpha:.18].CGColor;
 self.userInteractionEnabled=NO;
 _label=[UILabel new];_label.translatesAutoresizingMaskIntoConstraints=NO;
 _label.font=[UIFont monospacedDigitSystemFontOfSize:13 weight:UIFontWeightMedium];_label.textColor=UIColor.whiteColor;
 [self addSubview:_label];
 [NSLayoutConstraint activateConstraints:@[
  [_label.topAnchor constraintEqualToAnchor:self.topAnchor constant:7],[_label.bottomAnchor constraintEqualToAnchor:self.bottomAnchor constant:-7],
  [_label.leadingAnchor constraintEqualToAnchor:self.leadingAnchor constant:14],[_label.trailingAnchor constraintEqualToAnchor:self.trailingAnchor constant:-14]]];
 return self;
}
@end

#pragma mark - Interface

@interface MPViewController : UIViewController<UIImagePickerControllerDelegate,UINavigationControllerDelegate,UIDocumentPickerDelegate>
@end
@implementation MPViewController {
 AVSampleBufferDisplayLayer *_video;
 MPHomeView *_home;
 MPReceiver *_receiver;
 unsigned _decoderGen;
 MPNoticeView *_notice;
 UIButton *_dock;
 MPCamera *_camera;
 MPMic *_mic;
 MPCameraView *_camView;
 NSInteger _clipCount;
 MPSecureServer *_input;
 MPTouchView *_touch;
 NSNetService *_bonjour;
 NSTimer *_timer,*_statsTimer;
 MPHud *_hud;
 CGSize _streamSize;
 NSString *_encoderName;
 double _rttMs;
 int _liveMbps,_saturation;   // envoyés par le PC chaque seconde (débit adaptatif)
 BOOL _autoKb;                // clavier ouvert parce que le PC a signalé une zone de saisie
 MPSpeaker *_speaker;         // son du PC
 MPTouchBar *_bar;            // Touch Bar contextuelle
 CGFloat _zoom;CGPoint _zoomAt;UILabel *_zoomBadge;   // loupe (2 doigts maintenus)
 NSMutableDictionary<NSNumber*,NSFileHandle*> *_rxFiles;NSMutableDictionary<NSNumber*,NSString*> *_rxPaths;   // fichiers reçus du PC
 MPDashboardView *_dash;      // tableau de bord
 BOOL _remoteCam;             // caméra ouverte à la demande du PC (fenêtre « Caméras de l'iPad »)
 BOOL _soundOn;               // son demandé au PC pour cette connexion
 CGRect _fieldNorm;           // zone de saisie (coordonnées 0..1 de l'écran du PC)
 CGFloat _kbHeight;           // hauteur du clavier affiché
 BOOL _streaming;
 // rotation : un rideau aux couleurs de l'app masque la bascule du bureau Windows
 UIView *_curtain;
 BOOL _rotating,_streamAlive,_revealScheduled;
 unsigned _rotGen,_rotFromGen;
 NSString *_pendingOrientation;
}
- (UILabel*)label:(NSString*)t size:(CGFloat)s weight:(UIFontWeight)w color:(UIColor*)c {
 UILabel *l=[UILabel new];l.text=t;l.font=[UIFont systemFontOfSize:s weight:w];l.textColor=c;l.numberOfLines=0;l.textAlignment=NSTextAlignmentCenter;return l;
}
- (void)viewDidLoad {
 [super viewDidLoad];
 self.view.backgroundColor=UIColor.blackColor;
 _video=[AVSampleBufferDisplayLayer new];
 _video.videoGravity=AVLayerVideoGravityResizeAspect;
 _video.backgroundColor=UIColor.blackColor.CGColor;
 [self.view.layer addSublayer:_video];

 _input=[MPSecureServer new];
 _touch=[[MPTouchView alloc] initWithFrame:self.view.bounds];
 _touch.autoresizingMask=UIViewAutoresizingFlexibleWidth|UIViewAutoresizingFlexibleHeight;
 __weak MPSecureServer *weakInput=_input;
 __weak typeof(self) w=self;
 _touch.emit=^(NSString *line){[weakInput send:line];};
 _touch.onFourFingerTap=^{[w showSettings];};
 _touch.onThreeFingerSwipeUp=^{[weakInput send:@"WIN SWAP\n"];};
 _touch.onTwoFingerHold=^(CGPoint p){[w toggleZoomAt:p];};
 [self.view addSubview:_touch];

 _hud=[MPHud new];_hud.translatesAutoresizingMaskIntoConstraints=NO;_hud.hidden=YES;
 [self.view addSubview:_hud];

 // Touch Bar contextuelle (en bas) : les boutons changent avec l'application active du PC
 _bar=[[MPTouchBar alloc] initWithFrame:CGRectMake(0,self.view.bounds.size.height-MPTouchBarHeight,self.view.bounds.size.width,MPTouchBarHeight)];
 _bar.autoresizingMask=UIViewAutoresizingFlexibleWidth|UIViewAutoresizingFlexibleTopMargin;_bar.hidden=YES;
 _bar.collapsed=[MPDefaults() boolForKey:@"touchBarFolded"];
 _bar.onFold=^{typeof(self) s=w;if(!s)return;[UIView animateWithDuration:.25 animations:^{[s layoutContent];}];};
 [_bar setTitle:@"Bureau" items:@[@[@"Fenêtre ⇄ iPad",@"@swap"],@[@"Copier",@"ctrl+c"],@[@"Coller",@"ctrl+v"],@[@"Annuler",@"ctrl+z"],@[@"Pavé numérique",@"@numpad"]]];
 _bar.onAction=^(NSString *keys){
  if([keys isEqualToString:@"@swap"])[weakInput send:@"WIN SWAP\n"];
  else if([keys isEqualToString:@"@numpad"])[w showNumpad];
  else if([keys isEqualToString:@"@keyboard"]){typeof(self) s=w;if(s){if(s->_touch.isFirstResponder)[s->_touch resignFirstResponder];else [s showKeyboard];}}
  else [weakInput send:[NSString stringWithFormat:@"KS %@\n",MPEsc(keys)]];
 };
 [self.view addSubview:_bar];
 [NSLayoutConstraint activateConstraints:@[
  [_hud.topAnchor constraintEqualToAnchor:self.view.topAnchor constant:14],[_hud.trailingAnchor constraintEqualToAnchor:self.view.trailingAnchor constant:-14]]];

 // bouton flottant (logo) : ouvre les modes et réglages pendant l'utilisation
 _dock=[UIButton buttonWithType:UIButtonTypeCustom];_dock.translatesAutoresizingMaskIntoConstraints=NO;
 [_dock setImage:[UIImage imageNamed:@"Logo"] forState:UIControlStateNormal];
 _dock.imageView.contentMode=UIViewContentModeScaleAspectFill;
 _dock.layer.cornerRadius=24;_dock.clipsToBounds=YES;_dock.alpha=.4;_dock.hidden=YES;
 _dock.layer.borderWidth=1.5;_dock.layer.borderColor=[UIColor colorWithWhite:1 alpha:.6].CGColor;
 [_dock addTarget:self action:@selector(showSettings) forControlEvents:UIControlEventTouchUpInside];
 [self.view addSubview:_dock];
 [NSLayoutConstraint activateConstraints:@[
  [_dock.topAnchor constraintEqualToAnchor:self.view.topAnchor constant:14],[_dock.leadingAnchor constraintEqualToAnchor:self.view.leadingAnchor constant:14],
  [_dock.widthAnchor constraintEqualToConstant:48],[_dock.heightAnchor constraintEqualToConstant:48]]];

 _home=[[MPHomeView alloc] initWithFrame:self.view.bounds];
 _home.autoresizingMask=UIViewAutoresizingFlexibleWidth|UIViewAutoresizingFlexibleHeight;
 [_home.settingsButton addTarget:self action:@selector(showSettings) forControlEvents:UIControlEventTouchUpInside];
 [self.view addSubview:_home];

 _input.onAuthenticated=^(NSString *pc){(void)pc;[w controlConnected];};
 _input.onLine=^(NSString *line){[w controlLine:line];};
 _input.onPairing=^(NSString *pc,NSString *code){[w showNotice:@"Nouveau PC" text:[NSString stringWithFormat:@"« %@ » demande à utiliser cet iPad. Vérifiez que ce code s'affiche sur le PC, puis cliquez sur « Accepter » là-bas.",pc] code:code ok:YES];};
 _input.onNotice=^(NSString *title,NSString *text,BOOL ok){[w showNotice:title text:text code:nil ok:ok];};
 _input.onVideoStart=^(int fd){dispatch_async(dispatch_get_main_queue(),^{[w startDecoder:fd];});};
 _input.onBinary=^(uint8_t type,NSData *data){if(type==4)[w clipboardImage:data];else if(type==8)[w fileChunk:data];};
 _speaker=[MPSpeaker new];
 MPSpeaker *spk=_speaker;
 _input.onSound=^(NSData *pcm){[spk feed:pcm];};
 _input.onClosed=^{[w sensorsOff:NO];[w soundOff];};
 [_input start];
 [self applyPreferences];
 [self refreshLastPC];
 [self refreshAddress];
 [self publishBonjour];
 [[NSNotificationCenter defaultCenter] addObserver:self selector:@selector(appForeground) name:UIApplicationWillEnterForegroundNotification object:nil];
 [[NSNotificationCenter defaultCenter] addObserver:self selector:@selector(keyboardFrame:) name:UIKeyboardWillChangeFrameNotification object:nil];
 [[NSNotificationCenter defaultCenter] addObserver:self selector:@selector(keyboardHide:) name:UIKeyboardWillHideNotification object:nil];
 [[NSNotificationCenter defaultCenter] addObserver:self selector:@selector(appBackground) name:UIApplicationDidEnterBackgroundNotification object:nil];
 _timer=[NSTimer scheduledTimerWithTimeInterval:3 repeats:YES block:^(NSTimer *t){(void)t;[w refreshAddress];}];
 _statsTimer=[NSTimer scheduledTimerWithTimeInterval:1 repeats:YES block:^(NSTimer *t){(void)t;[w statsTick];}];
}

#pragma mark Flux vidéo et alertes
// Un décodeur par flux : à chaque relance de ffmpeg (rotation, qualité…) le PC envoie « V NEW »
- (void)startDecoder:(int)fd {
 MPReceiver *r=[MPReceiver new];
 unsigned gen=++_decoderGen;
 __weak typeof(self) w=self;__weak MPTouchView *weakTouch=_touch;
 r.onState=^(BOOL c){dispatch_async(dispatch_get_main_queue(),^{[w decoder:gen connected:c];});};
 r.onSize=^(CGSize sz){dispatch_async(dispatch_get_main_queue(),^{weakTouch.videoSize=sz;[w setStreamSize:sz];});};
 r.onSample=^(CMSampleBufferRef sb){CFRetain(sb);dispatch_async(dispatch_get_main_queue(),^{[w enqueue:sb gen:gen];CFRelease(sb);});};
 _receiver=r;
 [r runOnFd:fd];
}
- (void)decoder:(unsigned)gen connected:(BOOL)c {
 if(gen!=_decoderGen)return;
 _streamAlive=c;
 if(c){[self setConnected:YES];return;}
 if(_rotating)return;   // rotation en cours : le flux suivant arrive, on garde le rideau
 // fin de flux : on attend un peu avant d'afficher l'accueil (une relance est peut-être en cours)
 __weak typeof(self) w=self;
 dispatch_after(dispatch_time(DISPATCH_TIME_NOW,(int64_t)(1.5*NSEC_PER_SEC)),dispatch_get_main_queue(),^{
  typeof(self) s=w;if(s&&gen==s->_decoderGen&&!s->_rotating&&!s->_streamAlive)[s setConnected:NO];
 });
}
- (void)showNotice:(NSString*)title text:(NSString*)text code:(NSString*)code ok:(BOOL)ok {
 [_notice hide];
 _notice=[[MPNoticeView alloc] initWithTitle:title text:text code:code ok:ok];
 [_notice showIn:self.view autoHide:code?120:6];
}

#pragma mark Contrôle (canal avec le PC)
- (NSString*)orientationLine:(CGSize)size {return size.height>size.width?@"O P\n":@"O L\n";}
- (void)soundOff {_soundOn=NO;[_speaker stop];}
- (void)controlConnected {
 _soundOn=NO;[self syncSound];
 _clipCount=UIPasteboard.generalPasteboard.changeCount;   // on ne pousse pas l'ancien contenu à la connexion
 [_input send:[self orientationLine:self.view.bounds.size]];
 NSUserDefaults *d=MPDefaults();
 if([d boolForKey:@"qualityPending"]){
  [_input send:[NSString stringWithFormat:@"S %ld %ld\n",(long)[d integerForKey:@"fps"],(long)[d integerForKey:@"mbps"]]];
  [d setBool:NO forKey:@"qualityPending"];
 }
}
- (void)controlLine:(NSString*)line {
 NSArray<NSString*> *p=[line componentsSeparatedByString:@" "];
 NSString *k=p.firstObject;
 if([k isEqualToString:@"P"]&&p.count>=2){
  _rttMs=CACurrentMediaTime()*1000-p[1].doubleValue;
 } else if([k isEqualToString:@"N"]&&p.count>=2){
  [self rememberPC:[[p subarrayWithRange:NSMakeRange(1,p.count-1)] componentsJoinedByString:@" "] ip:_input.peer];
 } else if([k isEqualToString:@"CB"]&&p.count>=2){
  if([MPDefaults() boolForKey:@"clipboard"]){
   UIPasteboard.generalPasteboard.string=[p[1] stringByRemovingPercentEncoding]?:@"";
   _clipCount=UIPasteboard.generalPasteboard.changeCount;
  }
 } else if([k isEqualToString:@"MSG"]&&p.count>=2){
  [self showNotice:@"MirrorPad" text:[p[1] stringByRemovingPercentEncoding]?:@"" code:nil ok:NO];
 } else if([k isEqualToString:@"CAMREQ"]&&p.count>=2){
  [self remoteCamera:p];
 } else if([k isEqualToString:@"DB"]||[k isEqualToString:@"DBX"]){
  [_dash update:p];
 } else if([k isEqualToString:@"TB"]&&p.count>=2){
  // Touch Bar : « TB <titre> <libellé|touches>… » (encodés)
  NSMutableArray *items=[NSMutableArray array];
  for(NSUInteger i=2;i<p.count;i++){
   NSString *it=[p[i] stringByRemovingPercentEncoding];NSRange r=[it rangeOfString:@"|" options:NSBackwardsSearch];
   if(r.location!=NSNotFound)[items addObject:@[[it substringToIndex:r.location],[it substringFromIndex:r.location+1]]];
  }
  [_bar setTitle:[p[1] stringByRemovingPercentEncoding]?:@"" items:items];
 } else if([k isEqualToString:@"SC"]){
  // liste des raccourcis du PC : « libellé|touches » encodés, séparés par des espaces
  NSMutableArray *items=[NSMutableArray array];
  for(NSUInteger i=1;i<p.count;i++){
   NSString *it=[p[i] stringByRemovingPercentEncoding];NSRange r=[it rangeOfString:@"|" options:NSBackwardsSearch];
   if(r.location!=NSNotFound)[items addObject:@[[it substringToIndex:r.location],[it substringFromIndex:r.location+1]]];
  }
  if(items.count)[MPDefaults() setObject:items forKey:@"shortcuts"];
 } else if([k isEqualToString:@"SND"]&&p.count>=3&&[p[1] isEqualToString:@"FMT"]){
  [_speaker startRate:p[2].doubleValue];   // le PC annonce la fréquence avant le premier paquet
 } else if([k isEqualToString:@"KB"]){
  [self fieldLine:p];
 } else if([k isEqualToString:@"Q"]&&p.count>=3){
  _saturation=p[1].intValue;_liveMbps=p[2].intValue;
 } else if([k isEqualToString:@"C"]&&p.count>=4){
  NSUserDefaults *d=MPDefaults();
  if(![d boolForKey:@"qualityPending"]){[d setInteger:p[1].integerValue forKey:@"fps"];[d setInteger:p[2].integerValue forKey:@"mbps"];}
  _encoderName=[p[3] stringByRemovingPercentEncoding];
 }
}
- (void)rememberPC:(NSString*)name ip:(NSString*)ip {
 if(!name.length)return;
 NSUserDefaults *d=MPDefaults();
 NSMutableArray *h=[NSMutableArray array];
 [h addObject:@{@"name":name,@"ip":ip?:@"?",@"date":[NSDate date]}];
 for(NSDictionary *e in [d arrayForKey:@"history"])if(![e[@"name"] isEqual:name]&&h.count<5)[h addObject:e];
 [d setObject:h forKey:@"history"];
 [self refreshLastPC];
}
- (void)refreshLastPC {
 NSDictionary *e=[[MPDefaults() arrayForKey:@"history"] firstObject];
 _home.lastPCLabel.hidden=!e;
 if(e)_home.lastPCLabel.text=[NSString stringWithFormat:@"Dernier PC : %@ (%@), %@",e[@"name"],e[@"ip"],MPAgo(e[@"date"])];
}
- (void)applyPreferences {
 NSUserDefaults *d=MPDefaults();
 [self syncSound];
 _touch.longPressEnabled=[d boolForKey:@"longPress"];
 _touch.inertiaEnabled=[d boolForKey:@"inertia"];
 _touch.inputMode=[d integerForKey:@"inputMode"];
 _touch.penEraser=[d boolForKey:@"penEraser"];
 _dock.hidden=!_streaming;
 _hud.hidden=![d boolForKey:@"hud"]||!_streaming;
 BOOL barHidden=![d boolForKey:@"touchBar"]||!_streaming||_kbHeight>0;   // masquée pendant la saisie au clavier
 if(barHidden!=_bar.hidden){_bar.hidden=barHidden;[self layoutContent];}
}
- (void)showSettings {
 if(_touch.isFirstResponder)[_touch resignFirstResponder];
 MPSettingsPanel *panel=[MPSettingsPanel new];
 __weak typeof(self) w=self;
 panel.onChange=^{[w applyPreferences];};
 panel.onQuality=^(int fps,int mbps){[w requestQualityFps:fps mbps:mbps];};
 panel.onTrackpad=^{[w showTrackpad];};
 panel.onShortcuts=^{[w showShortcuts];};
 panel.onKeyboard=^{[w showKeyboard];};
 panel.onCamera=^{[w showCamera];};
 panel.onMic=^{[w toggleMic];};
 panel.onPhotos=^{[w pickPhoto];};
 panel.onFiles=^{[w pickFile];};
 panel.onDashboard=^{[w showDashboard];};
 [panel showIn:self.view];
}
- (void)showTrackpad {
 MPTrackpadView *t=[MPTrackpadView new];
 __weak MPSecureServer *in=_input;__weak typeof(self) w=self;
 t.emit=^(NSString *l){[in send:l];};
 t.onKeyboard=^{[w showKeyboard];};
 [t showIn:self.view];
}
- (void)showNumpad {
 MPNumpadView *v=[MPNumpadView new];
 __weak MPSecureServer *in=_input;
 v.emit=^(NSString *l){[in send:l];};
 [v showIn:self.view];
}
- (void)showShortcuts {
 MPShortcutsView *v=[MPShortcutsView new];
 __weak MPSecureServer *in=_input;
 v.emit=^(NSString *l){[in send:l];};
 [v showIn:self.view];
}
// Son du PC : demandé à la connexion (et quand le réglage change), coupé à la déconnexion
- (void)syncSound {
 BOOL want=_input.connected&&[MPDefaults() boolForKey:@"pcSound"];
 if(want==_soundOn)return;
 _soundOn=want;
 if(want)[_input send:@"SND START\n"];
 else {if(_input.connected)[_input send:@"SND STOP\n"];[_speaker stop];}
}
- (void)showKeyboard {_autoKb=NO;_fieldNorm=CGRectZero;[_touch becomeFirstResponder];}

#pragma mark Clavier automatique sur les zones de saisie du PC
// « KB 1 x y w h » : une zone de texte de l'écran de l'iPad vient de recevoir le focus ; « KB 0 » : plus de zone de saisie
- (void)fieldLine:(NSArray<NSString*>*)p {
 if(!_streaming)return;
 if(p.count>=6&&[p[1] isEqualToString:@"1"]){
  _fieldNorm=CGRectMake(p[2].doubleValue,p[3].doubleValue,p[4].doubleValue,p[5].doubleValue);
  if(!_touch.isFirstResponder){_autoKb=YES;[_touch becomeFirstResponder];}
  [self applyKeyboardShift:YES];
 } else if(p.count>=2&&[p[1] isEqualToString:@"0"]){
  _fieldNorm=CGRectZero;
  if(_autoKb&&_touch.isFirstResponder)[_touch resignFirstResponder];
  _autoKb=NO;
  [self applyKeyboardShift:YES];
 }
}
- (void)keyboardFrame:(NSNotification*)n {
 CGRect f=[self.view convertRect:[n.userInfo[UIKeyboardFrameEndUserInfoKey] CGRectValue] fromView:nil];
 _kbHeight=fmax(0,CGRectGetMaxY(self.view.bounds)-CGRectGetMinY(f));
 [self applyKeyboardShift:YES];[self applyPreferences];
}
- (void)keyboardHide:(NSNotification*)n {(void)n;_kbHeight=0;_autoKb=NO;[self applyKeyboardShift:YES];[self applyPreferences];}
// Remonte l'image (et la zone tactile) juste assez pour que la zone de saisie reste visible au-dessus du clavier
- (void)applyKeyboardShift:(BOOL)animated {
 CGFloat dy=0;
 if(_kbHeight>0&&_fieldNorm.size.height>0){
  CGRect v=self.view.bounds;
  if(_streamSize.width>0&&_streamSize.height>0)v=AVMakeRectWithAspectRatioInsideRect(_streamSize,self.view.bounds);
  CGFloat fieldBottom=v.origin.y+(_fieldNorm.origin.y+_fieldNorm.size.height)*v.size.height;
  CGFloat kbTop=self.view.bounds.size.height-_kbHeight;
  dy=fmin(_kbHeight,fmax(0,fieldBottom-kbTop+28));
 }
 // loupe : agrandissement autour du point choisi (coordonnées de la zone tactile), puis décalage « clavier »
 CGAffineTransform z=CGAffineTransformIdentity;
 if(_zoom>1){
  CGPoint c=CGPointMake(_touch.bounds.size.width/2,_touch.bounds.size.height/2);
  z=CGAffineTransformMake(_zoom,0,0,_zoom,(_zoomAt.x-c.x)*(1-_zoom),(_zoomAt.y-c.y)*(1-_zoom));
 }
 CGAffineTransform t=CGAffineTransformConcat(z,CGAffineTransformMakeTranslation(0,-dy));
 if(CGAffineTransformEqualToTransform(_touch.transform,t))return;
 [CATransaction begin];[CATransaction setAnimationDuration:animated?.25:0];
 _video.affineTransform=t;
 [CATransaction commit];
 [UIView animateWithDuration:animated?.25:0 animations:^{self->_touch.transform=t;}];
}

#pragma mark Loupe
// 2 doigts maintenus : agrandit ×2,5 autour du point ; on s'en sert normalement (1 doigt = clic précis), 2 doigts maintenus à nouveau = sortie
- (void)toggleZoomAt:(CGPoint)p {
 if(_zoom>1){_zoom=1;[_zoomBadge removeFromSuperview];_zoomBadge=nil;}
 else {
  _zoom=2.5;_zoomAt=p;
  _zoomBadge=MPLabel(@"  Loupe ×2,5 · maintenez 2 doigts pour quitter  ",14,UIFontWeightSemibold,1,0);
  _zoomBadge.backgroundColor=[UIColor colorWithWhite:0 alpha:.6];_zoomBadge.layer.cornerRadius=14;_zoomBadge.clipsToBounds=YES;
  [_zoomBadge sizeToFit];CGRect f=_zoomBadge.frame;f.size.height=28;f.origin=CGPointMake((self.view.bounds.size.width-f.size.width)/2,14);_zoomBadge.frame=f;
  _zoomBadge.autoresizingMask=UIViewAutoresizingFlexibleLeftMargin|UIViewAutoresizingFlexibleRightMargin;
  [self.view addSubview:_zoomBadge];
 }
 [self applyKeyboardShift:YES];
}

#pragma mark Fichiers reçus du PC (« Envoyer vers > iPad »)
// trame 8 : [id 4][drapeaux 1 : 1 début, 2 fin][début : longueur nom 2 + nom][données]
- (void)fileChunk:(NSData*)d {
 if(d.length<5)return;
 const uint8_t *b=d.bytes;uint32_t fid;memcpy(&fid,b,4);uint8_t flags=b[4];size_t off=5;
 if(!_rxFiles){_rxFiles=[NSMutableDictionary dictionary];_rxPaths=[NSMutableDictionary dictionary];}
 NSNumber *key=@(fid);
 if(flags&1){
  if(d.length<7)return;
  size_t n=(size_t)b[5]<<8|b[6];off=7+n;if(d.length<off)return;
  NSString *name=[[[NSString alloc] initWithBytes:b+7 length:n encoding:NSUTF8StringEncoding] lastPathComponent]?:@"fichier";
  NSString *dir=[NSHomeDirectory() stringByAppendingPathComponent:@"Documents/MirrorPad"];
  [NSFileManager.defaultManager createDirectoryAtPath:dir withIntermediateDirectories:YES attributes:nil error:nil];
  NSString *path=[dir stringByAppendingPathComponent:name];
  for(int i=2;[NSFileManager.defaultManager fileExistsAtPath:path];i++)
   path=[dir stringByAppendingPathComponent:[NSString stringWithFormat:@"%@ (%d).%@",name.stringByDeletingPathExtension,i,name.pathExtension]];
  [NSFileManager.defaultManager createFileAtPath:path contents:nil attributes:nil];
  NSFileHandle *h=[NSFileHandle fileHandleForWritingAtPath:path];if(!h)return;
  _rxFiles[key]=h;_rxPaths[key]=path;
  [self showNotice:@"Réception" text:[NSString stringWithFormat:@"%@…",name] code:nil ok:YES];
 }
 NSFileHandle *h=_rxFiles[key];if(!h)return;
 [h writeData:[d subdataWithRange:NSMakeRange(off,d.length-off)]];
 if(flags&2){
  [h closeFile];NSString *path=_rxPaths[key];[_rxFiles removeObjectForKey:key];[_rxPaths removeObjectForKey:key];
  [self fileReceived:path];
 }
}
// photos et vidéos -> Photos ; le reste -> menu de partage (« Enregistrer dans Fichiers », ouvrir dans une app…)
- (void)fileReceived:(NSString*)path {
 NSString *ext=path.pathExtension.lowercaseString;
 if([@[@"jpg",@"jpeg",@"png",@"heic",@"gif"] containsObject:ext]){
  UIImage *img=[UIImage imageWithContentsOfFile:path];
  if(img){UIImageWriteToSavedPhotosAlbum(img,nil,nil,nil);[self showNotice:@"Fichier reçu" text:[path.lastPathComponent stringByAppendingString:@" a été ajouté à Photos."] code:nil ok:YES];return;}
 }
 if([@[@"mov",@"mp4",@"m4v"] containsObject:ext]&&UIVideoAtPathIsCompatibleWithSavedPhotosAlbum(path)){
  UISaveVideoAtPathToSavedPhotosAlbum(path,nil,nil,nil);[self showNotice:@"Fichier reçu" text:[path.lastPathComponent stringByAppendingString:@" a été ajouté à Photos."] code:nil ok:YES];return;
 }
 UIActivityViewController *a=[[UIActivityViewController alloc] initWithActivityItems:@[[NSURL fileURLWithPath:path]] applicationActivities:nil];
 a.modalPresentationStyle=UIModalPresentationPopover;
 a.popoverPresentationController.sourceView=self.view;
 a.popoverPresentationController.sourceRect=CGRectMake(CGRectGetMidX(self.view.bounds),CGRectGetMidY(self.view.bounds),1,1);
 a.popoverPresentationController.permittedArrowDirections=0;
 [self presentViewController:a animated:YES completion:nil];
}

#pragma mark Fichiers envoyés au PC (app Fichiers de l'iPad)
- (void)pickFile {
 if(!_input.connected){[self showNotice:@"Fichiers" text:@"Connectez d'abord le PC." code:nil ok:NO];return;}
 UIDocumentPickerViewController *pk=[[UIDocumentPickerViewController alloc] initWithDocumentTypes:@[@"public.item"] inMode:UIDocumentPickerModeImport];
 pk.delegate=self;pk.allowsMultipleSelection=YES;
 [self presentViewController:pk animated:YES completion:nil];
}
- (void)documentPicker:(UIDocumentPickerViewController*)c didPickDocumentsAtURLs:(NSArray<NSURL*>*)urls {
 (void)c;
 MPSecureServer *input=_input;__weak typeof(self) w=self;
 [self showNotice:@"Fichiers" text:[NSString stringWithFormat:@"Envoi de %lu fichier(s) au PC…",(unsigned long)urls.count] code:nil ok:YES];
 dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED,0),^{
  unsigned long sent=0;
  for(NSURL *u in urls){
   NSInputStream *in=[NSInputStream inputStreamWithURL:u];[in open];
   uint32_t fid=arc4random();NSData *name=[u.lastPathComponent dataUsingEncoding:NSUTF8StringEncoding];
   if(name.length>1000)name=[name subdataWithRange:NSMakeRange(0,1000)];
   uint8_t *buf=malloc(256*1024);BOOL first=YES,ok=YES;
   NSNumber *size=nil;[u getResourceValue:&size forKey:NSURLFileSizeKey error:nil];
   unsigned long long total=size.unsignedLongLongValue,done=0;
   while(ok){
    while(input.pending>12&&input.connected)usleep(20000);   // on laisse partir les morceaux précédents
    if(!input.connected){ok=NO;break;}
    NSInteger n=[in read:buf maxLength:256*1024];if(n<0){ok=NO;break;}
    done+=(unsigned long long)n;BOOL last=n==0||done>=total;
    NSMutableData *f=[NSMutableData dataWithBytes:&fid length:4];uint8_t fl=(first?1:0)|(last?2:0);[f appendBytes:&fl length:1];
    if(first){uint8_t l[2]={(uint8_t)(name.length>>8),(uint8_t)name.length};[f appendBytes:l length:2];[f appendData:name];}
    [f appendBytes:buf length:(NSUInteger)n];
    [input sendBinary:8 data:f];first=NO;
    if(last)break;
   }
   free(buf);[in close];if(ok)sent++;
  }
  dispatch_async(dispatch_get_main_queue(),^{[w showNotice:@"Fichiers" text:[NSString stringWithFormat:@"%lu fichier(s) envoyé(s) dans Téléchargements > MirrorPad sur le PC.",sent] code:nil ok:sent==urls.count];});
 });
}

#pragma mark Tableau de bord
- (void)showDashboard {
 if(!_input.connected){[self showNotice:@"Tableau de bord" text:@"Connectez d'abord le PC." code:nil ok:NO];return;}
 _dash=[MPDashboardView new];
 __weak typeof(self) w=self;__weak MPSecureServer *in=_input;
 _dash.onClose=^{[in send:@"DB STOP\n"];typeof(self) s=w;if(s)s->_dash=nil;};
 [_dash showIn:self.view];
 [_input send:@"DB START\n"];
}

#pragma mark Capteurs : webcam, micro, photos, presse-papiers
- (void)showCamera {
 if(!_input.connected){[self showNotice:@"Webcam" text:@"Connectez d'abord le PC." code:nil ok:NO];return;}
 if(!_camera)_camera=[MPCamera new];
 if(!_camera.session){
  __weak MPSecureServer *in=_input;
  _camera.onFrame=^(NSData *f){[in sendBinary:3 data:f];};
  if(![_camera start:YES]){[self showNotice:@"Webcam" text:@"La caméra est indisponible. Autorisez MirrorPad dans Réglages > Confidentialité > Appareil photo." code:nil ok:NO];return;}
  [_input send:@"CAM START 1280 720 30"];
 }
 _camView=[MPCameraView new];
 __weak typeof(self) w=self;
 _camView.onSwitch=^{typeof(self) s=w;if(!s)return;[s->_camera start:!s->_camera.front];[s->_camView attach:s->_camera.session];};
 _camView.onStop=^{[w stopCamera];};
 [_camView attach:_camera.session];
 [_camView showIn:self.view];
 [self updateDock];
}
// Le PC veut voir une caméra de l'iPad (webcam sans fil) : « CAMREQ START front|back », « CAMREQ SWITCH », « CAMREQ STOP »
- (void)remoteCamera:(NSArray<NSString*>*)p {
 NSString *op=p[1];
 if([op isEqualToString:@"START"]){
  BOOL front=!(p.count>=3&&[p[2] isEqualToString:@"back"]);
  if(!_camera)_camera=[MPCamera new];
  BOOL wasRunning=_camera.session!=nil;
  if(!wasRunning||_camera.front!=front){
   __weak MPSecureServer *in=_input;
   _camera.onFrame=^(NSData *f){[in sendBinary:3 data:f];};
   if(![_camera start:front]){[_input send:[NSString stringWithFormat:@"MSG %@\n",MPEsc(@"Caméra de l'iPad indisponible (autorisation dans Réglages > Confidentialité > Appareil photo).")]];return;}
   if(_camView)[_camView attach:_camera.session];
  }
  if(!wasRunning)[_input send:@"CAM START 1280 720 30"];
  _remoteCam=YES;
  UIApplication.sharedApplication.idleTimerDisabled=YES;   // l'iPad ne se met pas en veille tant que le PC regarde
  [self showNotice:@"Caméra" text:[NSString stringWithFormat:@"Le PC affiche la caméra %@ de l'iPad.",front?@"avant":@"arrière"] code:nil ok:YES];
  [self updateDock];
 } else if([op isEqualToString:@"SWITCH"]&&_camera.session){
  [_camera start:!_camera.front];if(_camView)[_camView attach:_camera.session];
 } else if([op isEqualToString:@"STOP"]&&_remoteCam){
  _remoteCam=NO;UIApplication.sharedApplication.idleTimerDisabled=NO;
  if(!_camView.superview)[self stopCamera];   // l'utilisateur de l'iPad ne s'en sert pas lui-même : on coupe
 }
}
- (void)stopCamera {
 if(!_camera.session)return;
 [_camera stop];
 [_input send:@"CAM STOP"];
 [self updateDock];
}
- (void)toggleMic {
 if(!_mic)_mic=[MPMic new];
 if(_mic.running){
  [_mic stop];[_input send:@"MIC STOP"];
  [self showNotice:@"Micro" text:@"Le micro de l'iPad est coupé." code:nil ok:YES];
 } else {
  if(!_input.connected){[self showNotice:@"Micro" text:@"Connectez d'abord le PC." code:nil ok:NO];return;}
  __weak MPSecureServer *in=_input;
  _mic.onPCM=^(NSData *pcm){[in sendBinary:6 data:pcm];};
  if(![_mic start]){[self showNotice:@"Micro" text:@"Le micro est indisponible. Autorisez MirrorPad dans Réglages > Confidentialité > Micro." code:nil ok:NO];return;}
  [_input send:@"MIC START 48000 1"];
  [self showNotice:@"Micro actif" text:@"Sur le PC, choisissez « CABLE Output » comme micro dans Teams, Zoom… (pilote VB-CABLE)." code:nil ok:YES];
 }
 [self updateDock];
}
// arrêt de la caméra et du micro (session fermée, iPad verrouillé)
- (void)sensorsOff:(BOOL)notify {
 _remoteCam=NO;UIApplication.sharedApplication.idleTimerDisabled=NO;
 if(_camera.session){[_camera stop];if(notify)[_input send:@"CAM STOP"];}
 if(_mic.running){[_mic stop];if(notify)[_input send:@"MIC STOP"];}
 [_camView hide];_camView=nil;
 [self updateDock];
}
// bouton flottant cerclé de rouge tant que la webcam ou le micro fonctionnent
- (void)updateDock {
 BOOL live=_camera.session||_mic.running;
 _dock.layer.borderColor=(live?[UIColor colorWithRed:1 green:.25 blue:.3 alpha:1]:[UIColor colorWithWhite:1 alpha:.6]).CGColor;
 _dock.layer.borderWidth=live?3:1.5;_dock.alpha=live?.9:.4;
}
- (void)pickPhoto {
 if(!_input.connected){[self showNotice:@"Photos" text:@"Connectez d'abord le PC." code:nil ok:NO];return;}
 UIImagePickerController *pk=[UIImagePickerController new];
 pk.sourceType=UIImagePickerControllerSourceTypePhotoLibrary;pk.delegate=self;
 pk.modalPresentationStyle=UIModalPresentationPopover;   // obligatoire sur iPad pour la photothèque
 pk.popoverPresentationController.sourceView=self.view;
 pk.popoverPresentationController.sourceRect=CGRectMake(CGRectGetMidX(self.view.bounds),CGRectGetMidY(self.view.bounds),1,1);
 pk.popoverPresentationController.permittedArrowDirections=0;
 [self presentViewController:pk animated:YES completion:nil];
}
- (void)imagePickerController:(UIImagePickerController*)pk didFinishPickingMediaWithInfo:(NSDictionary<NSString*,id>*)info {
 [pk dismissViewControllerAnimated:YES completion:nil];
 NSURL *url=info[UIImagePickerControllerImageURL];   // fichier de la photo (iOS 11+), sinon l'image décodée
 UIImage *img=info[UIImagePickerControllerOriginalImage]?:info[UIImagePickerControllerEditedImage];
 if(!url&&!img){[self showNotice:@"Photos" text:@"Impossible de lire cette photo (elle est peut-être restée sur iCloud)." code:nil ok:NO];return;}
 [self showNotice:@"Photos" text:@"Envoi en cours…" code:nil ok:YES];
 __weak typeof(self) w=self;MPSecureServer *input=_input;
 dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED,0),^{
  NSData *data=url?[NSData dataWithContentsOfURL:url]:nil;
  NSString *ext=url.pathExtension.lowercaseString;
  if(!data.length||!ext.length){data=img?UIImageJPEGRepresentation(img,.92):nil;ext=@"jpg";}
  NSString *msg=nil;BOOL ok=NO;
  if(!data.length)msg=@"Impossible de lire cette photo.";
  else if(!input.connected)msg=@"Le PC s'est déconnecté : photo non envoyée.";
  else {
   NSDateFormatter *f=[NSDateFormatter new];f.dateFormat=@"'iPad_'yyyy-MM-dd_HH-mm-ss";
   NSData *name=[[NSString stringWithFormat:@"%@.%@",[f stringFromDate:[NSDate date]],ext] dataUsingEncoding:NSUTF8StringEncoding];
   NSMutableData *frame=[NSMutableData dataWithLength:2];
   uint8_t *h=frame.mutableBytes;h[0]=(uint8_t)(name.length>>8);h[1]=(uint8_t)name.length;
   [frame appendData:name];[frame appendData:data];
   if(frame.length>=(1u<<24))msg=@"Photo trop volumineuse (16 Mo maximum).";
   else {[input sendBinary:5 data:frame];ok=YES;
    msg=[NSString stringWithFormat:@"%.1f Mo envoyés. Elle arrive dans Téléchargements > MirrorPad sur le PC.",frame.length/1048576.0];}
  }
  dispatch_async(dispatch_get_main_queue(),^{[w showNotice:ok?@"Photo envoyée":@"Photos" text:msg code:nil ok:ok];});
 });
}
- (void)imagePickerControllerDidCancel:(UIImagePickerController*)pk {[pk dismissViewControllerAnimated:YES completion:nil];}
// Presse-papiers : vérifié chaque seconde (iOS 12 ne prévient pas des copies)
- (void)clipboardTick {
 if(!_input.connected||![MPDefaults() boolForKey:@"clipboard"])return;
 UIPasteboard *pb=UIPasteboard.generalPasteboard;
 if(pb.changeCount==_clipCount)return;
 _clipCount=pb.changeCount;
 if(pb.hasStrings){
  NSString *t=pb.string;
  if(t.length&&t.length<500000)[_input send:[@"CB " stringByAppendingString:MPEsc(t)]];
 } else if(pb.hasImages){
  NSData *png=UIImagePNGRepresentation(pb.image);
  if(png.length&&png.length<15*1024*1024)[_input sendBinary:4 data:png];
 }
}
- (void)clipboardImage:(NSData*)png {
 if(![MPDefaults() boolForKey:@"clipboard"])return;
 UIImage *img=[UIImage imageWithData:png];
 if(img){UIPasteboard.generalPasteboard.image=img;_clipCount=UIPasteboard.generalPasteboard.changeCount;}
}
- (void)requestQualityFps:(int)fps mbps:(int)mbps {
 if(_input.connected)[_input send:[NSString stringWithFormat:@"S %d %d\n",fps,mbps]];
 else [MPDefaults() setBool:YES forKey:@"qualityPending"];
}
- (void)setStreamSize:(CGSize)sz {_streamSize=sz;}
- (void)statsTick {
 [self clipboardTick];
 uint64_t bytes=0,frames=0;[_receiver takeBytes:&bytes frames:&frames];
 if(_input.connected)[_input send:[NSString stringWithFormat:@"P %.0f\n",CACurrentMediaTime()*1000]];
 if(_hud.hidden)return;
 NSMutableArray *parts=[NSMutableArray arrayWithObjects:
  [NSString stringWithFormat:@"%llu i/s",frames],
  [[NSString stringWithFormat:@"%.1f Mb/s",bytes*8/1e6] stringByReplacingOccurrencesOfString:@"." withString:@","],nil];
 if(_streamSize.width>0)[parts addObject:[NSString stringWithFormat:@"%.0f×%.0f",_streamSize.width,_streamSize.height]];
 if(_encoderName.length)[parts addObject:_encoderName];
 if(_liveMbps>0){
  NSInteger target=[MPDefaults() integerForKey:@"mbps"];
  [parts addObject:(target>0&&_liveMbps<target)?[NSString stringWithFormat:@"débit auto %d/%ld Mb/s",_liveMbps,(long)target]:[NSString stringWithFormat:@"débit %d Mb/s",_liveMbps]];
 }
 if(_rttMs>0)[parts addObject:[NSString stringWithFormat:@"réseau %.0f ms",_rttMs/2]];
 if(_saturation>=10)[parts addObject:[NSString stringWithFormat:@"saturation %d %%",_saturation]];
 _hud.label.text=[parts componentsJoinedByString:@"  ·  "];
}
- (void)viewWillTransitionToSize:(CGSize)size withTransitionCoordinator:(id<UIViewControllerTransitionCoordinator>)c {
 [super viewWillTransitionToSize:size withTransitionCoordinator:c];
 CGSize cur=self.view.bounds.size;
 if((size.height>size.width)==(cur.height>cur.width))return;   // demi-tour (180°) : rien à basculer côté PC
 if(_streaming)[self showCurtain];
 // anti-rebond : si l'iPad est tourné plusieurs fois de suite, on n'envoie que la position finale
 _pendingOrientation=[self orientationLine:size];
 [NSObject cancelPreviousPerformRequestsWithTarget:self selector:@selector(sendPendingOrientation) object:nil];
 [self performSelector:@selector(sendPendingOrientation) withObject:nil afterDelay:.2];
}
- (void)sendPendingOrientation {
 if(_pendingOrientation)[_input send:_pendingOrientation];   // le PC bascule l'écran virtuel en portrait / paysage
 _pendingOrientation=nil;
}
// Rideau de rotation : dégradé de l'app + logo, le temps que Windows réorganise le bureau
- (void)showCurtain {
 if(!_curtain){
  _curtain=[[UIView alloc] initWithFrame:self.view.bounds];
  _curtain.autoresizingMask=UIViewAutoresizingFlexibleWidth|UIViewAutoresizingFlexibleHeight;
  _curtain.userInteractionEnabled=NO;
  CAGradientLayer *g=MPBackdrop();g.frame=CGRectMake(0,0,4096,4096);[_curtain.layer addSublayer:g];
  UIImageView *logo=[[UIImageView alloc] initWithImage:[UIImage imageNamed:@"Logo"]];
  logo.frame=CGRectMake(0,0,72,72);logo.layer.cornerRadius=18;logo.clipsToBounds=YES;logo.tag=1;
  logo.autoresizingMask=UIViewAutoresizingFlexibleLeftMargin|UIViewAutoresizingFlexibleRightMargin|UIViewAutoresizingFlexibleTopMargin|UIViewAutoresizingFlexibleBottomMargin;
  [_curtain addSubview:logo];
  UIActivityIndicatorView *spin=[[UIActivityIndicatorView alloc] initWithActivityIndicatorStyle:UIActivityIndicatorViewStyleWhite];
  spin.tag=2;spin.autoresizingMask=logo.autoresizingMask;[spin startAnimating];
  [_curtain addSubview:spin];
 }
 _curtain.frame=self.view.bounds;
 UIView *logo=[_curtain viewWithTag:1],*spin=[_curtain viewWithTag:2];
 CGPoint mid=CGPointMake(CGRectGetMidX(_curtain.bounds),CGRectGetMidY(_curtain.bounds));
 logo.center=CGPointMake(mid.x,mid.y-20);spin.center=CGPointMake(mid.x,mid.y+46);
 if(!_curtain.superview){_curtain.alpha=0;[self.view insertSubview:_curtain belowSubview:_hud];}
 _rotating=YES;_revealScheduled=NO;_rotFromGen=_decoderGen;
 unsigned rg=++_rotGen;
 [_curtain.layer removeAllAnimations];
 [UIView animateWithDuration:.16 animations:^{self->_curtain.alpha=1;}];
 // sécurité : si le PC ne bascule pas (portrait non installé…), on rend la main après 8 s
 __weak typeof(self) w=self;
 dispatch_after(dispatch_time(DISPATCH_TIME_NOW,(int64_t)(8*NSEC_PER_SEC)),dispatch_get_main_queue(),^{
  typeof(self) s=w;if(s&&s->_rotGen==rg&&s->_rotating)[s hideCurtain];
 });
}
- (void)hideCurtain {
 _rotating=NO;_revealScheduled=NO;
 if(!_streamAlive){[self setConnected:NO];return;}   // plus de flux du tout : retour à l'accueil
 UIView *c=_curtain;if(!c.superview)return;
 [UIView animateWithDuration:.35 delay:0 options:UIViewAnimationOptionBeginFromCurrentState animations:^{c.alpha=0;}
                  completion:^(BOOL done){if(done&&c.alpha==0)[c removeFromSuperview];}];
}
- (void)viewDidLayoutSubviews {
 [super viewDidLayoutSubviews];
 [self layoutContent];
}
// Zone de l'image du PC : tout l'écran, moins la Touch Bar quand elle est dépliée (la barre des tâches reste visible)
- (CGRect)contentRect {
 CGRect r=self.view.bounds;
 if(_bar&&!_bar.hidden&&!_bar.collapsed)r.size.height-=MPTouchBarHeight;
 return r;
}
- (void)layoutContent {
 CGRect r=[self contentRect];
 [CATransaction begin];[CATransaction setDisableActions:YES];
 // bounds + position (et pas frame) : la translation éventuelle « clavier » reste appliquée
 _video.bounds=CGRectMake(0,0,r.size.width,r.size.height);_video.position=CGPointMake(CGRectGetMidX(r),CGRectGetMidY(r));
 [CATransaction commit];
 _touch.bounds=CGRectMake(0,0,r.size.width,r.size.height);_touch.center=CGPointMake(CGRectGetMidX(r),CGRectGetMidY(r));
 _bar.frame=CGRectMake(0,self.view.bounds.size.height-MPTouchBarHeight,self.view.bounds.size.width,MPTouchBarHeight);
}
- (void)enqueue:(CMSampleBufferRef)sb gen:(unsigned)gen {
 if(gen!=_decoderGen)return;   // image d'un flux précédent
 [self enqueue:sb];
 if(_rotating&&!_revealScheduled&&gen!=_rotFromGen){
  // première image du nouveau flux : on la laisse s'afficher puis on lève le rideau
  _revealScheduled=YES;unsigned rg=_rotGen;__weak typeof(self) w=self;
  dispatch_after(dispatch_time(DISPATCH_TIME_NOW,(int64_t)(.25*NSEC_PER_SEC)),dispatch_get_main_queue(),^{
   typeof(self) s=w;if(s&&s->_rotGen==rg&&s->_rotating)[s hideCurtain];
  });
 }
}
- (void)enqueue:(CMSampleBufferRef)sb {
 if(_video.status==AVQueuedSampleBufferRenderingStatusFailed){NSLog(@"MirrorPad: décodeur en erreur %@",_video.error);[_video flush];}
 [_video enqueueSampleBuffer:sb];
}
- (void)setConnected:(BOOL)c {
 _streaming=c;[self applyPreferences];
 MPHomeView *home=_home;
 if(c){
  [UIView animateWithDuration:.45 animations:^{home.alpha=0;home.transform=CGAffineTransformMakeScale(1.04,1.04);}
                   completion:^(BOOL done){if(done&&home.alpha==0)home.hidden=YES;}];
 } else {
  [_video flushAndRemoveImage];
  _rotating=NO;[_curtain.layer removeAllAnimations];[_curtain removeFromSuperview];
  home.hidden=NO;
  [UIView animateWithDuration:.45 animations:^{home.alpha=1;home.transform=CGAffineTransformIdentity;}];
 }
}
// iPad verrouillé / app en arrière-plan : on prévient le PC (il retire l'écran virtuel) puis on ferme la session
- (void)appBackground {
 _input.paused=YES;
 if(!_input.connected)return;
 [self sensorsOff:YES];   // iOS coupe la caméra et le micro en arrière-plan
 UIApplication *app=UIApplication.sharedApplication;
 __block UIBackgroundTaskIdentifier task=[app beginBackgroundTaskWithExpirationHandler:^{[app endBackgroundTask:task];task=UIBackgroundTaskInvalid;}];
 [_input send:@"SLEEP"];
 [_input disconnect];
 dispatch_after(dispatch_time(DISPATCH_TIME_NOW,(int64_t)(2*NSEC_PER_SEC)),dispatch_get_main_queue(),^{
  if(task!=UIBackgroundTaskInvalid){[app endBackgroundTask:task];task=UIBackgroundTaskInvalid;}
 });
}
// Retour : on accepte de nouveau le PC, qui se reconnecte et rebranche l'écran virtuel
- (void)appForeground {
 _input.paused=NO;
 [self publishBonjour];
}
// Annonce Bonjour « _mirrorpad._tcp » : le PC retrouve l'iPad sans connaître son adresse IP.
- (void)publishBonjour {
 [_bonjour stop];
 _bonjour=[[NSNetService alloc] initWithDomain:@"local." type:@"_mirrorpad._tcp." name:UIDevice.currentDevice.name port:MP_PORT];
 [_bonjour publish];
}
- (void)refreshAddress {_home.addressLabel.text=MPLocalIP();}
- (BOOL)prefersStatusBarHidden {return YES;}
- (BOOL)prefersHomeIndicatorAutoHidden {return YES;}
- (UIInterfaceOrientationMask)supportedInterfaceOrientations {return UIInterfaceOrientationMaskAll;}
@end

@interface MPAppDelegate : UIResponder<UIApplicationDelegate>
@property(nonatomic,strong) UIWindow *window;
@end
@implementation MPAppDelegate
- (BOOL)application:(UIApplication*)app didFinishLaunchingWithOptions:(NSDictionary*)options {
 (void)options;
 app.idleTimerDisabled=YES;
 self.window=[[UIWindow alloc] initWithFrame:UIScreen.mainScreen.bounds];
 self.window.rootViewController=[MPViewController new];
 [self.window makeKeyAndVisible];
 return YES;
}
@end

int main(int argc,char **argv){@autoreleasepool{
 signal(SIGPIPE,SIG_IGN);
 return UIApplicationMain(argc,argv,nil,NSStringFromClass(MPAppDelegate.class));
}}
