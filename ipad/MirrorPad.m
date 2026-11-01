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

#define MP_PORT 7270        // flux vidéo PC -> iPad
#define MP_INPUT_PORT 7271  // tactile iPad -> PC (le PC se connecte et lit des lignes texte)

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
 while(n){ssize_t r=recv(fd,p,n,0);if(r<=0){if(r<0&&errno==EINTR)continue;return NO;}p+=r;n-=(size_t)r;}
 return YES;
}
static uint32_t MPBE24(const uint8_t *p){return (uint32_t)p[0]<<16|(uint32_t)p[1]<<8|p[2];}
static uint32_t MPBE32(const uint8_t *p){return (uint32_t)p[0]<<24|MPBE24(p+1);}

#pragma mark - Réception réseau

@interface MPReceiver : NSObject
@property(nonatomic,copy) void (^onState)(BOOL connected);
@property(nonatomic,copy) void (^onSample)(CMSampleBufferRef sample);
@property(nonatomic,copy) void (^onSize)(CGSize size);
- (void)start;
- (void)takeBytes:(uint64_t*)bytes frames:(uint64_t*)frames; // compteurs depuis le dernier appel
@end
@implementation MPReceiver {
 CMVideoFormatDescriptionRef _format;
 uint8_t *_buf;size_t _cap;
 volatile uint64_t _bytes,_frames;
}
- (void)takeBytes:(uint64_t*)bytes frames:(uint64_t*)frames {*bytes=_bytes;*frames=_frames;_bytes=0;_frames=0;}
- (void)start {[NSThread detachNewThreadWithBlock:^{[self serve];}];}
- (void)serve {
 int srv=-1;
 for(;;){
  if(srv<0){srv=MPListen(MP_PORT);if(srv<0){sleep(1);continue;}}
  int c=accept(srv,NULL,NULL);
  if(c<0){if(errno!=EINTR){close(srv);srv=-1;sleep(1);}continue;} // socket récupéré par iOS après mise en veille : on le recrée
  int one=1;
  setsockopt(c,IPPROTO_TCP,TCP_NODELAY,&one,sizeof one);
  setsockopt(c,SOL_SOCKET,SO_NOSIGPIPE,&one,sizeof one);
  struct timeval tv={.tv_sec=10};setsockopt(c,SOL_SOCKET,SO_RCVTIMEO,&tv,sizeof tv);
  NSLog(@"MirrorPad: PC connecté");
  if(self.onState)self.onState(YES);
  [self readFLV:c];
  close(c);
  if(_format){CFRelease(_format);_format=NULL;}
  NSLog(@"MirrorPad: PC déconnecté");
  if(self.onState)self.onState(NO);
 }
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

// Canal tactile / contrôle (port 7271) : le PC s'y connecte.
//   iPad -> PC : gestes ("D x y" "M" "U" "R" "W n" "Z n" "T texte" "K BACK"), "O P|L" orientation,
//                "P n" mesure de latence, "S fps mbps" qualité demandée
//   PC -> iPad : "P n" réponse de latence, "N nom" nom du PC, "C fps mbps encodeur" réglages actifs
@interface MPInputServer : NSObject
@property(nonatomic,copy) void (^onLine)(NSString *line);      // appelé sur le fil principal
@property(nonatomic,copy) void (^onConnect)(NSString *peer);   // idem, avec l'adresse du PC
@property(nonatomic,readonly) BOOL connected;
@property(nonatomic,readonly,copy) NSString *peer;
- (void)start;
- (void)send:(NSString*)line;
@end
@implementation MPInputServer { int _client; unsigned _gen; }
- (instancetype)init {if((self=[super init]))_client=-1;return self;}
- (BOOL)connected {return _client>=0;}
- (void)start {[NSThread detachNewThreadWithBlock:^{[self serve];}];}
- (void)serve {
 int srv=-1;
 for(;;){
  if(srv<0){srv=MPListen(MP_INPUT_PORT);if(srv<0){sleep(1);continue;}}
  struct sockaddr_in from;socklen_t len=sizeof from;
  int c=accept(srv,(struct sockaddr*)&from,&len);
  if(c<0){if(errno!=EINTR){close(srv);srv=-1;sleep(1);}continue;}
  int one=1;
  setsockopt(c,IPPROTO_TCP,TCP_NODELAY,&one,sizeof one);
  setsockopt(c,SOL_SOCKET,SO_NOSIGPIPE,&one,sizeof one);
  char ip[INET_ADDRSTRLEN]="";inet_ntop(AF_INET,&from.sin_addr,ip,sizeof ip);
  NSString *peer=@(ip);
  dispatch_async(dispatch_get_main_queue(),^{
   if(self->_client>=0)close(self->_client);
   self->_client=c;unsigned gen=++self->_gen;self->_peer=peer;
   NSLog(@"MirrorPad: canal tactile connecté (%@)",peer);
   if(self.onConnect)self.onConnect(peer);
   [NSThread detachNewThreadWithBlock:^{[self readFrom:c gen:gen];}];
  });
 }
}
- (void)readFrom:(int)c gen:(unsigned)gen {
 NSMutableData *buf=[NSMutableData data];char tmp[2048];
 for(;;){
  ssize_t r=recv(c,tmp,sizeof tmp,0);
  if(r<=0)break;
  [buf appendBytes:tmp length:(NSUInteger)r];
  for(;;){
   const char *bytes=buf.bytes;const char *nl=memchr(bytes,'\n',buf.length);
   if(!nl)break;
   NSUInteger n=(NSUInteger)(nl-bytes);
   NSString *line=[[NSString alloc] initWithBytes:bytes length:n encoding:NSUTF8StringEncoding];
   [buf replaceBytesInRange:NSMakeRange(0,n+1) withBytes:NULL length:0];
   if(line.length)dispatch_async(dispatch_get_main_queue(),^{if(self.onLine)self.onLine(line);});
  }
 }
 dispatch_async(dispatch_get_main_queue(),^{
  if(self->_gen==gen&&self->_client>=0){close(self->_client);self->_client=-1;}
 });
}
- (void)send:(NSString*)line {
 if(_client<0)return;
 const char *s=line.UTF8String;size_t n=strlen(s);
 if(send(_client,s,n,0)!=(ssize_t)n){close(_client);_client=-1;}
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
@end
@implementation MPTouchView {
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
- (void)cancelLongPress {[NSObject cancelPreviousPerformRequestsWithTarget:self selector:@selector(longPress) object:nil];}
- (void)longPress {
 if(_mode!=1||_dragging)return;
 _longFired=YES;
 [self send:'R' at:_start];
 [self ripple:_start color:[UIColor colorWithRed:.55 green:.75 blue:1 alpha:1]];
}
// Molette : 12 points de déplacement = 40 unités Windows (120 = un cran)
- (void)scrollBy:(CGFloat)dy {
 _scrollAcc+=dy;
 int steps=(int)(_scrollAcc/12);
 if(steps){_scrollAcc-=steps*12;[self out:[NSString stringWithFormat:@"W %d\n",steps*40]];}
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
 NSArray *a=[self active:e];
 if(a.count>=3){
  [self cancelLongPress];
  if(_mode==1&&_dragging)[self send:'U' at:_last];
  if(_mode!=3&&_mode!=4)_moved=NO;
  _mode=a.count>=4?4:3;_centroidStart=[self centroid:a];_dragging=NO;
 } else if(_mode==0&&a.count==1){
  _mode=1;_dragging=NO;_longFired=NO;_primary=a[0];_start=_last=[a[0] locationInView:self];
  if(_longPressEnabled)[self performSelector:@selector(longPress) withObject:nil afterDelay:.55];
 } else if(a.count==2&&_mode!=3&&!(_mode==1&&(_dragging||_longFired))){
  [self cancelLongPress];
  _mode=2;_moved=NO;_decided=NO;_pinching=NO;_velocity=0;
  _centroid=_centroidStart=[self centroid:a];_scrollRef=_centroid.y;_scrollAcc=0;
  _pinchRef=[self spread:a];_lastMoveTime=e.timestamp;
 }
}
- (void)touchesMoved:(NSSet<UITouch*>*)touches withEvent:(UIEvent*)e {
 (void)touches;
 NSArray *a=[self active:e];
 if(_mode==1&&_primary){
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
 }
}
- (void)finish:(UIEvent*)e cancelled:(BOOL)cancelled {
 if([self active:e].count>0)return; // on attend que tous les doigts soient levés
 [self cancelLongPress];
 if(_mode==1){
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
 [d registerDefaults:@{@"longPress":@YES,@"inertia":@YES,@"hud":@NO,@"fps":@30,@"mbps":@12}];
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
- (void)showIn:(UIView*)parent;
@end
@implementation MPSettingsPanel {
 UISegmentedControl *_fps,*_quality;
 UISwitch *_longPress,*_inertia,*_hud;
 UIStackView *_history;
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

 _fps=[self segments:@[@"30 i/s",@"60 i/s"]];
 _quality=[self segments:@[@"Éco",@"Standard",@"Max"]];
 _longPress=[self toggle];_inertia=[self toggle];_hud=[self toggle];
 UILabel *note=MPLabel(@"La qualité est appliquée au PC dès qu'il est connecté.",13,UIFontWeightRegular,.6,0);
 _history=[UIStackView new];_history.axis=UILayoutConstraintAxisVertical;_history.spacing=6;

 UIStackView *st=[[UIStackView alloc] initWithArrangedSubviews:@[head,
   [self section:@"IMAGE"],[self row:@"Fluidité" control:_fps],[self row:@"Qualité" control:_quality],note,
   [self section:@"GESTES"],[self row:@"Appui long = clic droit" control:_longPress],[self row:@"Défilement avec élan" control:_inertia],
   [self section:@"AFFICHAGE"],[self row:@"Indicateur de performance" control:_hud],
   [self section:@"DERNIERS PC"],_history]];
 st.axis=UILayoutConstraintAxisVertical;st.spacing=12;st.translatesAutoresizingMaskIntoConstraints=NO;
 [st setCustomSpacing:22 afterView:head];
 for(UIView *v in st.arrangedSubviews)if([v isKindOfClass:UILabel.class]&&((UILabel*)v).font.pointSize==12)[st setCustomSpacing:8 afterView:v];
 [card.contentView addSubview:st];
 [NSLayoutConstraint activateConstraints:@[
  [card.centerXAnchor constraintEqualToAnchor:self.centerXAnchor],[card.centerYAnchor constraintEqualToAnchor:self.centerYAnchor],
  [card.widthAnchor constraintEqualToConstant:520],
  [st.topAnchor constraintEqualToAnchor:card.contentView.topAnchor constant:26],[st.bottomAnchor constraintEqualToAnchor:card.contentView.bottomAnchor constant:-26],
  [st.leadingAnchor constraintEqualToAnchor:card.contentView.leadingAnchor constant:30],[st.trailingAnchor constraintEqualToAnchor:card.contentView.trailingAnchor constant:-30],
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
 _longPress.on=[d boolForKey:@"longPress"];_inertia.on=[d boolForKey:@"inertia"];_hud.on=[d boolForKey:@"hud"];
 for(UIView *v in _history.arrangedSubviews){[_history removeArrangedSubview:v];[v removeFromSuperview];}
 NSArray *h=[d arrayForKey:@"history"];
 if(!h.count)[_history addArrangedSubview:MPLabel(@"Aucun PC pour l'instant.",15,UIFontWeightRegular,.6,0)];
 for(NSDictionary *e in h)
  [_history addArrangedSubview:MPLabel([NSString stringWithFormat:@"%@  ·  %@  ·  %@",e[@"name"],e[@"ip"],MPAgo(e[@"date"])],15,UIFontWeightRegular,.85,0)];
}
- (void)qualityChanged {
 int fps=_fps.selectedSegmentIndex==1?60:30,mbps=MPQualities[MAX(0,_quality.selectedSegmentIndex)];
 NSUserDefaults *d=MPDefaults();[d setInteger:fps forKey:@"fps"];[d setInteger:mbps forKey:@"mbps"];
 if(self.onQuality)self.onQuality(fps,mbps);
}
- (void)togglesChanged {
 NSUserDefaults *d=MPDefaults();
 [d setBool:_longPress.on forKey:@"longPress"];[d setBool:_inertia.on forKey:@"inertia"];[d setBool:_hud.on forKey:@"hud"];
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

@interface MPViewController : UIViewController
@end
@implementation MPViewController {
 AVSampleBufferDisplayLayer *_video;
 MPHomeView *_home;
 MPReceiver *_receiver;
 MPInputServer *_input;
 MPTouchView *_touch;
 NSNetService *_bonjour;
 NSTimer *_timer,*_statsTimer;
 MPHud *_hud;
 CGSize _streamSize;
 NSString *_encoderName;
 double _rttMs;
 BOOL _streaming;
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

 _input=[MPInputServer new];[_input start];
 _touch=[[MPTouchView alloc] initWithFrame:self.view.bounds];
 _touch.autoresizingMask=UIViewAutoresizingFlexibleWidth|UIViewAutoresizingFlexibleHeight;
 __weak MPInputServer *weakInput=_input;
 __weak typeof(self) w=self;
 _touch.emit=^(NSString *line){[weakInput send:line];};
 _touch.onFourFingerTap=^{[w showSettings];};
 [self.view addSubview:_touch];

 _hud=[MPHud new];_hud.translatesAutoresizingMaskIntoConstraints=NO;_hud.hidden=YES;
 [self.view addSubview:_hud];
 [NSLayoutConstraint activateConstraints:@[
  [_hud.topAnchor constraintEqualToAnchor:self.view.topAnchor constant:14],[_hud.trailingAnchor constraintEqualToAnchor:self.view.trailingAnchor constant:-14]]];

 _home=[[MPHomeView alloc] initWithFrame:self.view.bounds];
 _home.autoresizingMask=UIViewAutoresizingFlexibleWidth|UIViewAutoresizingFlexibleHeight;
 [_home.settingsButton addTarget:self action:@selector(showSettings) forControlEvents:UIControlEventTouchUpInside];
 [self.view addSubview:_home];

 _input.onConnect=^(NSString *peer){(void)peer;[w controlConnected];};
 _input.onLine=^(NSString *line){[w controlLine:line];};
 [self applyPreferences];
 [self refreshLastPC];
 _receiver=[MPReceiver new];
 _receiver.onState=^(BOOL c){dispatch_async(dispatch_get_main_queue(),^{[w setConnected:c];});};
 __weak MPTouchView *weakTouch=_touch;
 _receiver.onSize=^(CGSize sz){dispatch_async(dispatch_get_main_queue(),^{weakTouch.videoSize=sz;[w setStreamSize:sz];});};
 _receiver.onSample=^(CMSampleBufferRef sb){CFRetain(sb);dispatch_async(dispatch_get_main_queue(),^{[w enqueue:sb];CFRelease(sb);});};
 [_receiver start];
 [self refreshAddress];
 [self publishBonjour];
 [[NSNotificationCenter defaultCenter] addObserver:self selector:@selector(publishBonjour) name:UIApplicationWillEnterForegroundNotification object:nil];
 _timer=[NSTimer scheduledTimerWithTimeInterval:3 repeats:YES block:^(NSTimer *t){(void)t;[w refreshAddress];}];
 _statsTimer=[NSTimer scheduledTimerWithTimeInterval:1 repeats:YES block:^(NSTimer *t){(void)t;[w statsTick];}];
}

#pragma mark Contrôle (canal avec le PC)
- (NSString*)orientationLine:(CGSize)size {return size.height>size.width?@"O P\n":@"O L\n";}
- (void)controlConnected {
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
 _touch.longPressEnabled=[d boolForKey:@"longPress"];
 _touch.inertiaEnabled=[d boolForKey:@"inertia"];
 _hud.hidden=![d boolForKey:@"hud"]||!_streaming;
}
- (void)showSettings {
 if(_touch.isFirstResponder)[_touch resignFirstResponder];
 MPSettingsPanel *panel=[MPSettingsPanel new];
 __weak typeof(self) w=self;
 panel.onChange=^{[w applyPreferences];};
 panel.onQuality=^(int fps,int mbps){[w requestQualityFps:fps mbps:mbps];};
 [panel showIn:self.view];
}
- (void)requestQualityFps:(int)fps mbps:(int)mbps {
 if(_input.connected)[_input send:[NSString stringWithFormat:@"S %d %d\n",fps,mbps]];
 else [MPDefaults() setBool:YES forKey:@"qualityPending"];
}
- (void)setStreamSize:(CGSize)sz {_streamSize=sz;}
- (void)statsTick {
 uint64_t bytes=0,frames=0;[_receiver takeBytes:&bytes frames:&frames];
 if(_input.connected)[_input send:[NSString stringWithFormat:@"P %.0f\n",CACurrentMediaTime()*1000]];
 if(_hud.hidden)return;
 NSMutableArray *parts=[NSMutableArray arrayWithObjects:
  [NSString stringWithFormat:@"%llu i/s",frames],
  [[NSString stringWithFormat:@"%.1f Mb/s",bytes*8/1e6] stringByReplacingOccurrencesOfString:@"." withString:@","],nil];
 if(_streamSize.width>0)[parts addObject:[NSString stringWithFormat:@"%.0f×%.0f",_streamSize.width,_streamSize.height]];
 if(_encoderName.length)[parts addObject:_encoderName];
 if(_rttMs>0)[parts addObject:[NSString stringWithFormat:@"réseau %.0f ms",_rttMs/2]];
 _hud.label.text=[parts componentsJoinedByString:@"  ·  "];
}
- (void)viewWillTransitionToSize:(CGSize)size withTransitionCoordinator:(id<UIViewControllerTransitionCoordinator>)c {
 [super viewWillTransitionToSize:size withTransitionCoordinator:c];
 [_input send:[self orientationLine:size]]; // le PC bascule l'écran virtuel en portrait / paysage
}
- (void)viewDidLayoutSubviews {
 [super viewDidLayoutSubviews];
 [CATransaction begin];[CATransaction setDisableActions:YES];_video.frame=self.view.bounds;[CATransaction commit];
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
  home.hidden=NO;
  [UIView animateWithDuration:.45 animations:^{home.alpha=1;home.transform=CGAffineTransformIdentity;}];
 }
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
 return UIApplicationMain(argc,argv,nil,NSStringFromClass(MPAppDelegate.class));
}}
