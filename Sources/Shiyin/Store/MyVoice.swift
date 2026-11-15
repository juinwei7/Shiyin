import Foundation

/// 錄一段自己的聲音（design §56、R80）
extension AppModel {
    /// 算聲紋、存成「我」的一份 enroll 聲紋
    func enrollVoice(name: String, samples: [Float]) async throws {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        let embedding = try await VoiceEnrollment.voiceprint(samples)
        try db.enrollVoiceprint(name: trimmed, embedding: embedding)
        reloadPeople()
        noticeMessage = String(localized: "已記住你的聲音：之後的會議聽到像你的聲音，會帶入「\(trimmed)」或在待確認問你")
    }

    /// 只刪那份聲紋，這個人和會議裡的名字照舊（R80.4）
    func deleteEnrollment() {
        perform { try db.deleteEnrollment() }
        reloadPeople()
    }

    /// 名字的預設值：已經錄過的那個人，不然是這台電腦的使用者名稱（R80.1）
    var enrollmentDefaultName: String {
        people.first(where: { $0.enrolledAt != nil })?.name ?? NSFullUserName()
    }
}
