import SwiftUI

/// First-run setup, shown only when no library exists: choose a source folder, optionally add the
/// TMDB token, then run the setup check. Skipping leaves the library empty; nothing is forced.
struct FirstRunSetupView: View {
  @ObservedObject var coordinator: AppCoordinator
  @Binding var isPresented: Bool

  private enum Step: Int, CaseIterable { case welcome, source, metadata, check }

  @State private var step: Step = .welcome
  @State private var sourceName = "Movies"
  @State private var sourceAddress = ""
  @State private var sourceAdded = false
  @State private var sourceMessage: String?
  @State private var adding = false
  @State private var token = ""
  @State private var tokenSaved = false
  @State private var tokenMessage: String?
  @State private var checks: [SetupCheck.Result] = []

  var body: some View {
    VStack(alignment: .leading, spacing: 16) {
      HStack(spacing: 6) {
        ForEach(Step.allCases, id: \.rawValue) { item in
          Circle()
            .fill(item == step ? MyraColor.text : MyraColor.line)
            .frame(width: 7, height: 7)
        }
      }
      content
      Spacer(minLength: 0)
      footer
    }
    .padding(26)
    .frame(width: 560, height: 440)
  }

  @ViewBuilder private var content: some View {
    switch step {
    case .welcome:
      VStack(alignment: .leading, spacing: 12) {
        Text("Welcome to Myra").font(.system(size: 24, weight: .bold))
        Text("Your personal library, player and downloader. Three quick steps and you are watching.")
          .foregroundStyle(MyraColor.text2)
        setupLine("1 · Choose a source folder", "A web address that lists your videos", "server.rack")
        setupLine("2 · Connect metadata", "Posters and details from TMDB, optional", "book")
        setupLine("3 · Check your setup", "Sources, download folder and metadata", "checkmark.shield")
      }
    case .source:
      VStack(alignment: .leading, spacing: 12) {
        Text("Choose a source folder").font(.system(size: 20, weight: .bold))
        Text("Paste the address of a folder listing, such as an Apache, nginx or h5ai index. Indexing starts in the background.")
          .foregroundStyle(MyraColor.text2)
        TextField("Name, for example Home NAS", text: $sourceName)
          .textFieldStyle(.roundedBorder)
        HStack {
          TextField("http://nas.local/movies/", text: $sourceAddress)
            .textFieldStyle(.roundedBorder)
            .onSubmit { Task { await addSource() } }
          Button(sourceAdded ? "Added" : "Add") { Task { await addSource() } }
            .buttonStyle(MyraButtonStyle(prominent: true))
            .disabled(sourceAdded || adding || sourceAddress.trimmingCharacters(in: .whitespaces).isEmpty)
        }
        if adding { ProgressView().controlSize(.small) }
        if let sourceMessage {
          Text(sourceMessage).font(.caption).foregroundStyle(sourceAdded ? MyraColor.text2 : .orange)
        }
      }
    case .metadata:
      VStack(alignment: .leading, spacing: 12) {
        Text("Posters and details").font(.system(size: 20, weight: .bold))
        Text("Myra can fetch posters, ratings, genres and cast from TMDB. Only title names and years are sent. It is optional: filters and shelves also work from folder names.")
          .foregroundStyle(MyraColor.text2)
        HStack {
          SecureField("TMDB API Read Access Token", text: $token)
            .textFieldStyle(.roundedBorder)
          Button(tokenSaved ? "Saved" : "Save in Keychain") { saveToken() }
            .buttonStyle(MyraButtonStyle())
            .disabled(tokenSaved || token.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
        }
        if let tokenMessage {
          Text(tokenMessage).font(.caption).foregroundStyle(MyraColor.text2)
        }
        Text("You can add or remove the token later in Settings → Library.")
          .font(.caption).foregroundStyle(MyraColor.text2)
      }
    case .check:
      VStack(alignment: .leading, spacing: 10) {
        Text("Setup check").font(.system(size: 20, weight: .bold))
        Text("Here is what Myra can read on this \(MyraDevice.name).").foregroundStyle(MyraColor.text2)
        VStack(spacing: 0) {
          ForEach(checks) { result in
            HStack(spacing: 10) {
              Image(systemName: symbol(result.status))
                .foregroundStyle(color(result.status))
              VStack(alignment: .leading, spacing: 2) {
                Text(result.title).font(.system(size: 13))
                Text(result.detail).font(.system(size: 11)).foregroundStyle(MyraColor.text2)
              }
              Spacer()
            }
            .padding(10)
            Divider()
          }
        }
        .background(MyraColor.content, in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(MyraColor.line, lineWidth: 1))
      }
      .onAppear(perform: runChecks)
    }
  }

  private var footer: some View {
    HStack {
      if step == .welcome {
        Button("Skip for now") { isPresented = false }.buttonStyle(MyraButtonStyle())
      } else {
        Button("Back") { move(-1) }.buttonStyle(MyraButtonStyle())
      }
      Spacer()
      if step == .check {
        Button("Open Myra") { isPresented = false }
          .buttonStyle(MyraButtonStyle(prominent: true))
          .keyboardShortcut(.defaultAction)
      } else {
        Button("Continue") { move(1) }
          .buttonStyle(MyraButtonStyle(prominent: true))
          .keyboardShortcut(.defaultAction)
          .disabled(step == .source && !sourceAdded)
      }
    }
  }

  private func setupLine(_ title: String, _ detail: String, _ symbol: String) -> some View {
    HStack(spacing: 12) {
      Image(systemName: symbol).frame(width: 22).foregroundStyle(MyraColor.text2)
      VStack(alignment: .leading, spacing: 2) {
        Text(title).font(.system(size: 13, weight: .semibold))
        Text(detail).font(.system(size: 11)).foregroundStyle(MyraColor.text2)
      }
    }
  }

  private func move(_ delta: Int) {
    guard let next = Step(rawValue: step.rawValue + delta) else { return }
    step = next
  }

  /// Validates the address and adds the source. Indexing then runs in the background.
  private func addSource() async {
    let check = SetupCheck.sourceAddressCheck(sourceAddress)
    guard check.status == .ok else {
      sourceMessage = check.detail
      return
    }
    adding = true
    defer { adding = false }
    let added = await coordinator.addCategory(name: sourceName, urlText: sourceAddress)
    sourceAdded = added
    sourceMessage = added
      ? "Reachable. Indexing starts in the background, and you can keep using Myra."
      : "The folder could not be read. Check the address and that it lists videos."
  }

  private func saveToken() {
    let value = token.trimmingCharacters(in: .whitespacesAndNewlines)
    guard MyraSettingsValidation.tokenMessage(value) == nil else {
      tokenMessage = "Paste the read access token without spaces or line breaks."
      return
    }
    do {
      try TMDBKeyStore.save(value)
      tokenSaved = true
      token = ""
      tokenMessage = "Saved in Keychain."
    } catch {
      tokenMessage = error.localizedDescription
    }
  }

  private func runChecks() {
    var results = [SetupCheck.sourceAddressCheck(sourceAddress)]
    results.append(
      SetupCheck.readabilityResult(name: sourceName, readable: sourceAdded, message: sourceMessage))
    results.append(
      SetupCheck.downloadFolderCheck(path: coordinator.settings?.downloadDirectory ?? ""))
    results.append(
      SetupCheck.Result(
        id: "metadata", title: "TMDB metadata", status: tokenSaved ? .ok : .warning,
        detail: tokenSaved ? "Token saved in Keychain." : "Not set. Filters and shelves still work from folder names."))
    checks = results
  }

  private func symbol(_ status: SetupCheck.Result.Status) -> String {
    switch status {
    case .ok: "checkmark.circle.fill"
    case .warning: "exclamationmark.circle.fill"
    case .failed: "xmark.octagon.fill"
    }
  }

  private func color(_ status: SetupCheck.Result.Status) -> Color {
    switch status {
    case .ok: .green
    case .warning: .orange
    case .failed: .red
    }
  }
}
