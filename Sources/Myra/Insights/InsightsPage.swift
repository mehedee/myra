#if canImport(AppKit)
import AppKit
#else
import UIKit
#endif
import SwiftUI

/// The Insights page: hours watched, watch streak, top genre and the most watched titles, all
/// computed on this Mac from the watch history. Share Summary copies a plain-text summary.
struct InsightsPage: View {
  @ObservedObject var store: EntertainmentStore
  @State private var period: InsightsPeriod = .thisYear
  @State private var copied = false
  @Environment(\.myraAccent) private var accent

  private var summary: InsightsSummary {
    InsightsSummary.make(
      samples: InsightsHistory.samples(catalogue: store.catalogue, personal: store.personal),
      watchedTitleIDs: store.personal.watched, period: period, now: .now, calendar: .current)
  }

  var body: some View {
    let summary = self.summary
    ScrollView {
      VStack(alignment: .leading, spacing: 18) {
        banner(summary)
        Picker("Period", selection: $period) {
          ForEach(InsightsPeriod.allCases) { Text($0.title).tag($0) }
        }
        .pickerStyle(.segmented)
        .labelsHidden()
        .frame(maxWidth: 320)
        .accessibilityLabel("Period")
        .onChange(of: period) { _, _ in copied = false }

        if summary.hasHistory {
          kpis(summary)
          HStack(alignment: .top, spacing: 18) {
            monthlyChart(summary)
              .frame(maxWidth: .infinity, alignment: .topLeading)
            mostWatched(summary)
              .frame(maxWidth: .infinity, alignment: .topLeading)
          }
        } else {
          ContentUnavailableView(
            "No watch history yet", systemImage: "chart.bar",
            description: Text("Play a title and your hours, streak and favourites appear here."))
            .frame(minHeight: 280)
        }

        Text(
          "Insights are computed on this \(MyraDevice.name) from your watch history. Nothing is uploaded. Hours count the furthest point each file reached."
        )
        .font(.system(size: 12))
        .foregroundStyle(MyraColor.text2)
      }
      .padding(.horizontal, HomeLayout.pageInset)
      .padding(.top, 14)
      .padding(.bottom, 30)
    }
  }

  private func banner(_ summary: InsightsSummary) -> some View {
    HStack(spacing: 12) {
      Image(systemName: "sparkles")
        .font(.system(size: 18))
        .foregroundStyle(accent.color)
      VStack(alignment: .leading, spacing: 3) {
        Text(summary.period == .allTime ? "Your whole history" : "Your \(summary.periodLabel)")
          .font(.system(size: 14, weight: .semibold))
        Text(
          "You watched \(hoursText(summary.hoursWatched)) hours across \(summary.titlesFinished) finished \(summary.titlesFinished == 1 ? "title" : "titles")."
        )
        .font(.system(size: 12))
        .foregroundStyle(MyraColor.text2)
      }
      Spacer(minLength: 12)
      if copied {
        Text("Copied to the clipboard")
          .font(.system(size: 12))
          .foregroundStyle(MyraColor.text2)
      }
      Button("Share Summary", action: { shareSummary(summary) })
        .buttonStyle(MyraButtonStyle())
    }
    .padding(14)
    .background(accent.color.opacity(0.12), in: RoundedRectangle(cornerRadius: 12))
  }

  private func kpis(_ summary: InsightsSummary) -> some View {
    LazyVGrid(columns: [GridItem(.adaptive(minimum: 150), spacing: 12)], spacing: 12) {
      kpi(value: "\(hoursText(summary.hoursWatched)) h", label: "Hours watched")
      kpi(value: "\(summary.titlesFinished)", label: "Titles finished")
      kpi(
        value: "\(summary.currentStreakDays) \(summary.currentStreakDays == 1 ? "day" : "days")",
        label: "Current watch streak")
      if let genre = summary.topGenre {
        kpi(
          value: genre.name,
          label: "Top genre · \(Int((genre.share * 100).rounded()))% of hours")
      } else {
        kpi(value: "None yet", label: "Top genre")
      }
    }
  }

  private func kpi(value: String, label: String) -> some View {
    VStack(alignment: .leading, spacing: 4) {
      Text(value)
        .font(.system(size: 20, weight: .bold))
        .lineLimit(1)
        .minimumScaleFactor(0.7)
      Text(label)
        .font(.system(size: 12))
        .foregroundStyle(MyraColor.text2)
        .lineLimit(2)
    }
    .frame(maxWidth: .infinity, alignment: .leading)
    .padding(14)
    .background(MyraColor.fill2, in: RoundedRectangle(cornerRadius: 12))
    .accessibilityElement(children: .combine)
  }

  @ViewBuilder private func monthlyChart(_ summary: InsightsSummary) -> some View {
    VStack(alignment: .leading, spacing: 10) {
      Text("Hours per month")
        .font(.system(size: 14, weight: .semibold))
      if summary.hoursByMonth.isEmpty {
        Text("Monthly hours are shown for a single year.")
          .font(.system(size: 12))
          .foregroundStyle(MyraColor.text2)
          .frame(maxWidth: .infinity, minHeight: 140, alignment: .leading)
      } else {
        let peak = max(summary.hoursByMonth.max() ?? 0, 0.1)
        let symbols = Calendar.current.veryShortMonthSymbols
        HStack(alignment: .bottom, spacing: 6) {
          ForEach(Array(summary.hoursByMonth.enumerated()), id: \.offset) { index, hours in
            VStack(spacing: 4) {
              RoundedRectangle(cornerRadius: 3)
                .fill(accent.color.opacity(hours > 0 ? 0.85 : 0.15))
                .frame(height: max(3, 120 * hours / peak))
              Text(symbols[index])
                .font(.system(size: 10))
                .foregroundStyle(MyraColor.text3)
            }
            .frame(maxWidth: .infinity)
            .help("\(hoursText(hours)) hours")
          }
        }
        .frame(height: 150, alignment: .bottom)
        .accessibilityElement(children: .combine)
        .accessibilityLabel("Hours per month for \(summary.periodLabel)")
      }
    }
    .padding(14)
    .background(MyraColor.fill2, in: RoundedRectangle(cornerRadius: 12))
  }

  private func mostWatched(_ summary: InsightsSummary) -> some View {
    VStack(alignment: .leading, spacing: 10) {
      Text("Most watched")
        .font(.system(size: 14, weight: .semibold))
      if summary.mostWatched.isEmpty {
        Text("Nothing watched in this period yet.")
          .font(.system(size: 12))
          .foregroundStyle(MyraColor.text2)
      } else {
        ForEach(summary.mostWatched) { title in
          HStack(spacing: 10) {
            Text(title.name)
              .font(.system(size: 13, weight: .medium))
              .lineLimit(1)
            Spacer(minLength: 8)
            Text(title.kind == .series ? "Series" : "Movie")
              .font(.system(size: 11, weight: .semibold))
              .foregroundStyle(MyraColor.text2)
              .padding(.horizontal, 6)
              .padding(.vertical, 1)
              .background(MyraColor.fill2, in: RoundedRectangle(cornerRadius: 4))
            Text("\(hoursText(title.hours)) h")
              .font(.system(size: 12))
              .foregroundStyle(MyraColor.text2)
              .monospacedDigit()
          }
          .padding(.vertical, 4)
        }
      }
    }
    .padding(14)
    .background(MyraColor.fill2, in: RoundedRectangle(cornerRadius: 12))
  }

  private func shareSummary(_ summary: InsightsSummary) {
    PlatformServices.copy(summary.shareText())
    copied = true
  }

  private func hoursText(_ hours: Double) -> String {
    hours >= 10 ? String(format: "%.0f", hours) : String(format: "%.1f", hours)
  }
}
