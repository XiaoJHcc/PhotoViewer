import SwiftUI

struct DemoPhoto: Identifiable, Hashable {
    let id: Int
    let palette: Int
    let aspectRatio: CGFloat
    let capturedAt: String
    let sortDate: Int
    let fileSize: Int
    let aperture: String
    let shutter: String
    let iso: String
    let focalLength: String
    var rating: Int

    var name: String {
        String(format: "DSC_%04d.ARW", 1840 + id)
    }
}

enum DemoSortKey: String, CaseIterable, Identifiable {
    case name
    case captureDate
    case fileSize

    var id: String { rawValue }

    var title: String {
        switch self {
        case .name: "名称"
        case .captureDate: "拍摄时间"
        case .fileSize: "文件大小"
        }
    }
}

enum DemoRatingFilter: Hashable, CaseIterable, Identifiable {
    case all
    case unrated
    case exact(Int)
    case atLeast(Int)
    case rejected
    case conflict

    var id: String { title }

    var title: String {
        switch self {
        case .all: "全部"
        case .unrated: "无星级"
        case .exact(let value): "\(value) 星"
        case .atLeast(let value): "\(value) 星以上"
        case .rejected: "已拒绝"
        case .conflict: "星级冲突"
        }
    }

    static var allCases: [DemoRatingFilter] {
        [.all, .unrated] + (1...5).map { .exact($0) } + (1...4).map { .atLeast($0) } + [.rejected, .conflict]
    }

    func matches(_ rating: Int) -> Bool {
        switch self {
        case .all: true
        case .unrated: rating == 0
        case .exact(let value): rating == value
        case .atLeast(let value): rating >= value
        case .rejected: rating == -1
        case .conflict: false
        }
    }
}

struct DemoCenteringRequest: Equatable {
    let id = UUID()
    let photoID: Int
}

@MainActor
final class DemoWorkspace: ObservableObject {
    @Published private(set) var photos: [DemoPhoto]
    @Published private(set) var selectedID: Int
    @Published var sortKey: DemoSortKey = .name
    @Published var ascending = true
    @Published var ratingFilter: DemoRatingFilter = .all
    @Published var showFilmstrip = true
    @Published var showControls = true
    @Published var showAnalysis = false
    @Published var showSimilarity = false
    @Published var enhanced = false
    @Published var isZoomed = false
    @Published private(set) var centeringRequest: DemoCenteringRequest?

    init() {
        let ratings = [3, 0, 4, 5, 2, 4, 0, 3, 5, 1, 4, 3]
        let aspects: [CGFloat] = [1.5, 1.5, 0.8, 1.5, 1.333, 1.5, 0.8, 1.5, 1.5, 1.333, 1.5, 0.8]
        let apertures = ["f/1.8", "f/2.8", "f/4", "f/5.6"]
        let shutters = ["1/125", "1/250", "1/500", "1/60"]
        let isoValues = ["100", "200", "400", "800"]
        let focalLengths = ["24mm", "35mm", "50mm", "75mm"]
        var items: [DemoPhoto] = []
        for index in 0..<12 {
            let day = (index % 9) + 1
            let hour = 7 + (index % 10)
            let minute = (index * 7) % 60
            items.append(DemoPhoto(
                id: index,
                palette: index % 6,
                aspectRatio: aspects[index],
                capturedAt: String(format: "10-%02d %02d:%02d", day, hour, minute),
                sortDate: (day * 24 + hour) * 60 + minute,
                fileSize: 24_000_000 + index * 1_730_000,
                aperture: apertures[index % 4],
                shutter: shutters[index % 4],
                iso: isoValues[index % 4],
                focalLength: focalLengths[index % 4],
                rating: ratings[index]
            ))
        }
        photos = items
        selectedID = 3
    }

    var selectedPhoto: DemoPhoto {
        photos.first { $0.id == selectedID } ?? photos[0]
    }

    var visiblePhotos: [DemoPhoto] {
        let filtered = photos.filter { ratingFilter.matches($0.rating) }
        return filtered.sorted { first, second in
            let lhs = ascending ? first : second
            let rhs = ascending ? second : first
            switch sortKey {
            case .name:
                return lhs.name.localizedCaseInsensitiveCompare(rhs.name) == .orderedAscending
            case .captureDate:
                return lhs.sortDate == rhs.sortDate ? lhs.id < rhs.id : lhs.sortDate < rhs.sortDate
            case .fileSize:
                return lhs.fileSize == rhs.fileSize ? lhs.id < rhs.id : lhs.fileSize < rhs.fileSize
            }
        }
    }

    func select(_ photo: DemoPhoto) {
        selectedID = photo.id
        isZoomed = false
    }

    func selectPrevious() {
        step(by: -1)
    }

    func selectNext() {
        step(by: 1)
    }

    private func step(by offset: Int) {
        let items = visiblePhotos
        guard !items.isEmpty else { return }
        guard let current = items.firstIndex(where: { $0.id == selectedID }) else {
            select(items[0])
            centeringRequest = DemoCenteringRequest(photoID: items[0].id)
            return
        }
        let target = current + offset
        guard items.indices.contains(target) else { return }
        select(items[target])
        centeringRequest = DemoCenteringRequest(photoID: items[target].id)
    }

    func setRating(_ rating: Int, photoID: Int? = nil) {
        let targetID = photoID ?? selectedID
        guard let index = photos.firstIndex(where: { $0.id == targetID }) else { return }
        photos[index].rating = rating
    }

    func rating(of photo: DemoPhoto) -> Int {
        photos.first { $0.id == photo.id }?.rating ?? photo.rating
    }

    func toggleZoom() {
        isZoomed.toggle()
    }
}
