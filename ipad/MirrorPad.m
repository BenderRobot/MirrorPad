// MirrorPad 0.3 — reçoit un flux H.264 (conteneur FLV sur TCP) envoyé par le PC
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
@end
@implementation MPReceiver {
 CMVideoFormatDescriptionRef _format;
 uint8_t *_buf;size_t _cap;
}
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
 if(self.onSample)self.onSample(sb);
 CFRelease(sb);
}
@end

#pragma mark - Tactile

// Serveur du canal tactile : le PC s'y connecte ; on lui envoie des lignes
//   "D x y" appui, "M x y" déplacement, "U x y" relâchement, "R x y" clic droit, "W n" molette
// x et y sont normalisés (0..1) dans l'image du PC.
@interface MPInputServer : NSObject
- (void)start;
- (void)send:(NSString*)line;
@end
@implementation MPInputServer { int _client; }
- (instancetype)init {if((self=[super init]))_client=-1;return self;}
- (void)start {[NSThread detachNewThreadWithBlock:^{[self serve];}];}
- (void)serve {
 int srv=-1;
 for(;;){
  if(srv<0){srv=MPListen(MP_INPUT_PORT);if(srv<0){sleep(1);continue;}}
  int c=accept(srv,NULL,NULL);
  if(c<0){if(errno!=EINTR){close(srv);srv=-1;sleep(1);}continue;}
  int one=1;
  setsockopt(c,IPPROTO_TCP,TCP_NODELAY,&one,sizeof one);
  setsockopt(c,SOL_SOCKET,SO_NOSIGPIPE,&one,sizeof one);
  dispatch_async(dispatch_get_main_queue(),^{
   if(self->_client>=0)close(self->_client);
   self->_client=c;
   NSLog(@"MirrorPad: canal tactile connecté");
  });
 }
}
- (void)send:(NSString*)line {
 if(_client<0)return;
 const char *s=line.UTF8String;size_t n=strlen(s);
 if(send(_client,s,n,0)!=(ssize_t)n){close(_client);_client=-1;}
}
@end

// Gestes : 1 doigt tapé = clic, 1 doigt glissé = glisser (bouton maintenu),
//          2 doigts tapés = clic droit, 2 doigts glissés verticalement = molette.
@interface MPTouchView : UIView
@property(nonatomic,copy) void (^emit)(NSString *line);
@property(nonatomic) CGSize videoSize;
@end
@implementation MPTouchView {
 int _mode;          // 0 rien, 1 un doigt, 2 deux doigts
 BOOL _dragging,_moved;
 __weak UITouch *_primary;
 CGPoint _start,_last,_centroid;
 CGFloat _scrollRef,_scrollAcc,_scrollTotal;
}
- (instancetype)initWithFrame:(CGRect)f {
 if((self=[super initWithFrame:f])){self.multipleTouchEnabled=YES;self.backgroundColor=UIColor.clearColor;}
 return self;
}
- (CGPoint)norm:(CGPoint)p {
 CGRect r=self.bounds;
 if(_videoSize.width>0&&_videoSize.height>0)r=AVMakeRectWithAspectRatioInsideRect(_videoSize,self.bounds);
 CGFloat x=(p.x-r.origin.x)/r.size.width,y=(p.y-r.origin.y)/r.size.height;
 return CGPointMake(fmin(fmax(x,0),1),fmin(fmax(y,0),1));
}
- (void)send:(char)c at:(CGPoint)p {
 CGPoint n=[self norm:p];
 if(self.emit)self.emit([NSString stringWithFormat:@"%c %.5f %.5f\n",c,n.x,n.y]);
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
- (void)touchesBegan:(NSSet<UITouch*>*)touches withEvent:(UIEvent*)e {
 (void)touches;
 NSArray *a=[self active:e];
 if(_mode==0&&a.count==1){_mode=1;_dragging=NO;_primary=a[0];_start=_last=[a[0] locationInView:self];}
 else if(a.count>=2&&!(_mode==1&&_dragging)){_mode=2;_moved=NO;_centroid=[self centroid:a];_scrollRef=_centroid.y;_scrollAcc=0;_scrollTotal=0;}
}
- (void)touchesMoved:(NSSet<UITouch*>*)touches withEvent:(UIEvent*)e {
 (void)touches;
 NSArray *a=[self active:e];
 if(_mode==1&&_primary){
  CGPoint p=[_primary locationInView:self];
  if(!_dragging&&hypot(p.x-_start.x,p.y-_start.y)>8){_dragging=YES;[self send:'D' at:_start];}
  if(_dragging)[self send:'M' at:p];
  _last=p;
 } else if(_mode==2&&a.count>=2){
  CGPoint c=[self centroid:a];_centroid=c;
  CGFloat dy=c.y-_scrollRef;_scrollRef=c.y;_scrollAcc+=dy;_scrollTotal+=fabs(dy);
  if(_scrollTotal>6)_moved=YES;
  int steps=(int)(_scrollAcc/12);
  if(steps){_scrollAcc-=steps*12;if(self.emit)self.emit([NSString stringWithFormat:@"W %d\n",steps*40]);}
 }
}
- (void)finish:(UIEvent*)e cancelled:(BOOL)cancelled {
 if([self active:e].count>0)return; // on attend que tous les doigts soient levés
 if(_mode==1){
  if(_dragging)[self send:'U' at:_last];
  else if(!cancelled){[self send:'D' at:_start];[self send:'U' at:_start];}
 } else if(_mode==2&&!_moved&&!cancelled){
  [self send:'R' at:_centroid];
 }
 _mode=0;_dragging=NO;_primary=nil;
}
- (void)touchesEnded:(NSSet<UITouch*>*)touches withEvent:(UIEvent*)e {(void)touches;[self finish:e cancelled:NO];}
- (void)touchesCancelled:(NSSet<UITouch*>*)touches withEvent:(UIEvent*)e {(void)touches;[self finish:e cancelled:YES];}
@end

#pragma mark - Interface

@interface MPViewController : UIViewController
@end
@implementation MPViewController {
 AVSampleBufferDisplayLayer *_video;
 UIView *_waiting;
 UILabel *_address;
 MPReceiver *_receiver;
 MPInputServer *_input;
 MPTouchView *_touch;
 NSTimer *_timer;
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
 _touch.emit=^(NSString *line){[weakInput send:line];};
 [self.view addSubview:_touch];

 _waiting=[UIView new];_waiting.translatesAutoresizingMaskIntoConstraints=NO;
 _waiting.backgroundColor=[UIColor colorWithRed:.035 green:.065 blue:.11 alpha:1];
 _waiting.userInteractionEnabled=NO; // l'écran d'attente laisse passer les touchers
 [self.view addSubview:_waiting];
 UIStackView *st=[UIStackView new];st.axis=UILayoutConstraintAxisVertical;st.spacing=18;st.alignment=UIStackViewAlignmentCenter;st.translatesAutoresizingMaskIntoConstraints=NO;
 [_waiting addSubview:st];
 UIColor *grey=[UIColor colorWithWhite:.68 alpha:1];
 [st addArrangedSubview:[self label:@"MirrorPad" size:48 weight:UIFontWeightBold color:UIColor.whiteColor]];
 [st addArrangedSubview:[self label:@"En attente du PC…" size:24 weight:UIFontWeightMedium color:[UIColor colorWithRed:1 green:.79 blue:.38 alpha:1]]];
 _address=[self label:@"" size:30 weight:UIFontWeightSemibold color:[UIColor colorWithRed:.4 green:.8 blue:1 alpha:1]];
 [st addArrangedSubview:_address];
 [st addArrangedSubview:[self label:@"Sur le PC, lancez MirrorPad-Sender.ps1 avec cette adresse." size:18 weight:UIFontWeightRegular color:grey]];
 [NSLayoutConstraint activateConstraints:@[
  [_waiting.topAnchor constraintEqualToAnchor:self.view.topAnchor],[_waiting.bottomAnchor constraintEqualToAnchor:self.view.bottomAnchor],
  [_waiting.leadingAnchor constraintEqualToAnchor:self.view.leadingAnchor],[_waiting.trailingAnchor constraintEqualToAnchor:self.view.trailingAnchor],
  [st.centerXAnchor constraintEqualToAnchor:_waiting.centerXAnchor],[st.centerYAnchor constraintEqualToAnchor:_waiting.centerYAnchor],
  [st.widthAnchor constraintLessThanOrEqualToAnchor:_waiting.widthAnchor constant:-60]]];

 __weak typeof(self) w=self;
 _receiver=[MPReceiver new];
 _receiver.onState=^(BOOL c){dispatch_async(dispatch_get_main_queue(),^{[w setConnected:c];});};
 __weak MPTouchView *weakTouch=_touch;
 _receiver.onSize=^(CGSize sz){dispatch_async(dispatch_get_main_queue(),^{weakTouch.videoSize=sz;});};
 _receiver.onSample=^(CMSampleBufferRef sb){CFRetain(sb);dispatch_async(dispatch_get_main_queue(),^{[w enqueue:sb];CFRelease(sb);});};
 [_receiver start];
 [self refreshAddress];
 _timer=[NSTimer scheduledTimerWithTimeInterval:3 repeats:YES block:^(NSTimer *t){(void)t;[w refreshAddress];}];
}
- (void)viewDidLayoutSubviews {
 [super viewDidLayoutSubviews];
 [CATransaction begin];[CATransaction setDisableActions:YES];_video.frame=self.view.bounds;[CATransaction commit];
}
- (void)enqueue:(CMSampleBufferRef)sb {
 if(_video.status==AVQueuedSampleBufferRenderingStatusFailed){NSLog(@"MirrorPad: décodeur en erreur %@",_video.error);[_video flush];}
 [_video enqueueSampleBuffer:sb];
}
- (void)setConnected:(BOOL)c {_waiting.hidden=c;if(!c)[_video flushAndRemoveImage];}
- (void)refreshAddress {_address.text=[NSString stringWithFormat:@"%@  ·  port %d",MPLocalIP(),MP_PORT];}
- (BOOL)prefersStatusBarHidden {return YES;}
- (BOOL)prefersHomeIndicatorAutoHidden {return YES;}
- (UIInterfaceOrientationMask)supportedInterfaceOrientations {return UIInterfaceOrientationMaskLandscape;}
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
