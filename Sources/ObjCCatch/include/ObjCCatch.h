#import <Foundation/Foundation.h>

NS_ASSUME_NONNULL_BEGIN

/// 執行 block，接住 Objective-C 的例外（Swift 的 do/catch 接不到），回傳例外的名稱和原因；沒有例外時是 nil。
/// AVAudioEngine 在裝置的格式中途改變時會丟例外（design §50.3、D160）
NSString *_Nullable ShiyinCatch(NS_NOESCAPE void (^block)(void));

NS_ASSUME_NONNULL_END
