# CoGaze GitHub Issues 作成スクリプト
# 実行前に: winget install GitHub.cli && gh auth login
# 実行方法: .\create_github_issues.ps1

$repo = "Fialuxe/CoGaze"

# --- ラベル作成 ---
$labels = @(
    @{ name = "test:hardware"; color = "e11d48"; description = "実機(Quest3)での動作確認が必要" },
    @{ name = "test:software"; color = "7c3aed"; description = "ソフトウェア単体でテスト可能" },
    @{ name = "test:prerequisite"; color = "0284c7"; description = "他セットアップが前提のテスト" },
    @{ name = "impl"; color = "059669"; description = "未実装・未完了の機能" },
    @{ name = "inspector-required"; color = "d97706"; description = "Unityエディター上のInspector設定が必要" }
)

foreach ($label in $labels) {
    gh label create $label.name --color $label.color --description $label.description --repo $repo 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Label already exists or created: $($label.name)"
    }
}

Write-Host "`n=== Creating 8 Issue Groups ===`n"

# ============================================================
# Issue 1: 音声 (Voice/Audio)
# ============================================================
$issue1_body = @"
## 概要
PhotonVoice2 (PV2) による双方向音声の実機確認。

## 前提
- Quest3 ビルド済み、Expert は Editor 起動
- PC の「デスクトップアプリのマイクアクセス」が ON
- ``cogaze_config.json`` の ``microphoneDevice`` を実マイク名に設定

## テスト手順・確認項目

### 🔊 Worker → Expert（HMD→PC）
- [ ] Worker（Quest）で喋る → Expert（PC）で聞こえるか
- [ ] DebugHUD（Yボタン）の **TX●（送信中）** が点灯するか
- [ ] Expert 側の **RX●（受信中）** が点灯するか
- [ ] ``cogaze_*.log`` に ``voice_remote RMS > 300`` が出るか（実音声の目安）

### 🔊 Expert → Worker（PC→HMD）
- [ ] Expert（PC）で喋る → Worker（HMD）で聞こえるか
- [ ] Expert の **TX●** が点灯するか
- [ ] Worker 側の **RX●** が点灯するか
- [ ] Expert ``voice_local`` の RMS が > 300（PCマイク動作確認）

### 📐 Spatial Audio 確認
- [ ] Worker 側で Expert の声が小さければ ``Speaker.spatialBlend = 0``（2D化）を検討
- [ ] ハウリングが起きないことを確認

## 判定基準
- 双方向で相手の声が明瞭に聞こえる → ✅ PASS
- TX●/RX● インジケータが動作している → ✅ PASS
"@

gh issue create `
    --repo $repo `
    --title "[TEST-1] 音声 双方向確認 (Worker↔Expert Voice)" `
    --body $issue1_body `
    --label "test:hardware"
Write-Host "✅ Issue 1 created: Voice"

# ============================================================
# Issue 2: 視線 (Gaze)
# ============================================================
$issue2_body = @"
## 概要
Expert 視線の OSC 受信から Worker 表示同期までのパイプライン確認。

## 前提（必須）
- Tobii アイトラッカー接続済み
- Python OSC サーバー起動済み（``/gaze`` エンドポイント稼働）
- Python 未起動の場合は Expert 視線が原点表示になるため検証不可

## テスト手順・確認項目

### 👁️ Expert 視線 OSC 受信
- [ ] ``[OscGazeInput] Received /gaze`` ログが cogaze_*.log に出るか
- [ ] OscGazeInput.cs のファイル名が正しくリネームされているか確認（旧バグ修正済みのはず）

### 👁️ Worker 側 GazeVisualizer
- [ ] Worker 起動時に ``GazeVisualizer`` が生成されているか（``SceneBootstrapper2.SetupWorker`` ログ確認）
- [ ] Worker HMD 上で Expert の視線マーカーが表示されるか

### 🔄 GazeMode 条件切替
- [ ] Ray / Circle / Frustum / NoGaze の各モードで表示が切り替わるか
- [ ] ``GazeHandler.CurrentMode`` → ``PhotonSerializeView`` で Worker に同期されるか

## 判定基準
- Worker HMD 上で Expert の視線位置が追従表示される → ✅ PASS
- GazeMode 切替が Worker に反映される → ✅ PASS
"@

gh issue create `
    --repo $repo `
    --title "[TEST-2] 視線 Expert→Worker Gaze パイプライン確認" `
    --body $issue2_body `
    --label "test:hardware","test:prerequisite"
Write-Host "✅ Issue 2 created: Gaze"

# ============================================================
# Issue 3: QR認識・単一QR較正
# ============================================================
$issue3_body = @"
## 概要
QR コードによる空間較正（単一 QR モード）の端から端までの確認。

## テスト手順・確認項目

### 🔍 QR 検出
- [ ] Worker（Quest）で QR をカメラに映す
- [ ] ``[QRSpatialManager] QR DETECTED id='...'`` ログが出るか
- [ ] Worker 側でマーカー（球）が URP 材質で **可視化** されるか

### 📐 較正フロー（順序厳守）
1. [ ] Worker でグリップ長押し → 較正 A 確定
2. [ ] ``calib_`` で始まるペイロードの QR をスキャン
3. [ ] SharedMesh（部屋メッシュ）が物理 QR の位置に自動整列するか
4. [ ] Expert 側でも SharedMesh が同期移動するか（``RPC_ReceiveMeshTransform`` ログ確認）

### 🔄 QR RESET（再初期化）
- [ ] DebugHUD（Yボタン/Tab）表示中に **QR RESET ボタンをタッチ** または右手Bボタン
- [ ] 両者（Worker/Expert）でマーカーが消去されるか
- [ ] 再スキャンで再配置されるか

### ⚠️ Expert 位置整合確認
- [ ] 較正 **後** に QR スキャン → Expert のマーカーが正しい位置に出るか
- [ ] 較正 **前** に QR スキャン → ずれる（既知の運用上の注意。バグではない）

## 判定基準
- Worker と Expert 両方で QR マーカーが物理 QR に重なる → ✅ PASS
- QR RESET 後に再スキャンで正しく再配置される → ✅ PASS
"@

gh issue create `
    --repo $repo `
    --title "[TEST-3] QR認識・単一QR較正 空間同期確認" `
    --body $issue3_body `
    --label "test:hardware","inspector-required"
Write-Host "✅ Issue 3 created: QR Single"

# ============================================================
# Issue 4: 2QRキャリブレーション
# ============================================================
$issue4_body = @"
## 概要
2 枚の QR を使った自動キャリブレーションシステムの確認。

## ⚙️ 事前 Inspector 設定（必須）
Unity エディターで ``[Environment]`` オブジェクトの ``MeshHandler`` コンポーネントに設定:
- ``Calibration QR Id A`` = ``"QR_A"``
- ``Calibration QR Id B`` = ``"QR_B"``
- ``Indicator A`` = SharedMesh の子 Transform（物理 QR-A 設置予定位置）
- ``Indicator B`` = SharedMesh の子 Transform（物理 QR-B 設置予定位置）

indicatorA/B が **どちらも null** の場合は legacy 単一 QR モードで動作（フォールバック）。

## テスト手順・確認項目

### 🧪 Unity Test Runner（PC のみ）
- [ ] Window → General → Test Runner → EditMode → Run All
- [ ] ``DualQRCalibrationTests`` の **10件すべて PASS** することを確認

### 📱 実機 2QR キャリブ
1. [ ] Worker（Quest）で **QR_A** をスキャン → HUD に「QR_A スキャン済」表示
2. [ ] Worker で **QR_B** をスキャン → SharedMesh 自動整列 + HUD に「較正完了」
3. [ ] **A/B どちらを先にスキャンしても** 正しく動作するか（XOR 条件）
4. [ ] Expert 側でも SharedMesh が同期整列するか

### 🔑 Expert 確認
- [ ] Expert で **M キー** → Worker のメッシュが表示/非表示切替されるか
- [ ] ビデオ越しにズレを目視確認できるか

### ↩️ リセット
- [ ] ``ResetDualCalibration()`` 呼び出し後に A/B スキャンをやり直せるか

## 判定基準
- 10 件 EditMode テスト全 PASS → ✅ PASS
- 実機で QR_A/QR_B スキャン後に SharedMesh が正確に整列 → ✅ PASS
"@

gh issue create `
    --repo $repo `
    --title "[TEST-4] 2QRキャリブレーション 実機動作確認" `
    --body $issue4_body `
    --label "test:hardware","test:software","inspector-required"
Write-Host "✅ Issue 4 created: Dual QR"

# ============================================================
# Issue 5: NASA-TLX アンケート
# ============================================================
$issue5_body = @"
## 概要
NASA-TLX アンケートの VR ポーク入力（指先タッチ操作）の動作確認。

## テスト手順・確認項目

### 👆 ポーク入力
- [ ] TLX パネルが ``0.5m`` 前方に表示されているか（panelDistance 設定値）
- [ ] 人差し指先でボタンを触る（ポーク） → ボタンが反応するか
- [ ] **白フラッシュ**（0.1 秒）が確認できるか
- [ ] **ハプティクス振動**（強・0.6/0.9 強度）が感じられるか
- [ ] コントローラ先端でもポーク反応するか

### 📏 距離感・UX
- [ ] 0.5m のパネル距離が近すぎ/遠すぎないか（感覚チェック）
- [ ] 「触れている間はヘッド追従を停止（IsEngaged）」が機能するか

### ⚠️ 既知バグ確認（修正未着手）
- [ ] Expert 側でアンケートをスキップ（強制非表示）した時、Worker 側パネルが残らないか確認
  - 残る場合は「Worker 側を隠す RPC が未実装」のため、別 issue で対応予定

## 判定基準
- ポーク → 白フラッシュ + 振動が出る → ✅ PASS
- 7 段階評価を 7 問すべて回答できる → ✅ PASS
"@

gh issue create `
    --repo $repo `
    --title "[TEST-5] NASA-TLX ポーク入力・フィードバック確認" `
    --body $issue5_body `
    --label "test:hardware"
Write-Host "✅ Issue 5 created: NASA-TLX"

# ============================================================
# Issue 6: Python OSC連携
# ============================================================
$issue6_body = @"
## 概要
Python OSC サーバーと CoGaze 間の制御イベント疎通確認。

## 前提
- Python OSC サーバーが PC で起動済み
- Quest と PC が同一 WiFi ネットワーク上にある

## テスト手順・確認項目

### 🔌 疎通確認
- [ ] ``/ping`` 送信 → ``/pong`` 応答が返るか
- [ ] cogaze_*.log に ``[OSC]`` ログが出るか

### 🧪 実験フロー
- [ ] Python から ``/trial_start`` 送信 → CoGaze 側でタスク開始するか
- [ ] ``/trial_end`` 送信 → タスク終了・ログ記録されるか
- [ ] ``/calibration`` イベントのフロー確認

### 📊 ログ確認
- [ ] OSC イベントのタイムスタンプが cogaze_*.log に記録されているか
- [ ] Worker/Expert どちら側に届いているか確認

## 判定基準
- ping/pong 往復成功 → ✅ PASS
- trial_start/end トリガーで実験フロー開始/終了 → ✅ PASS
"@

gh issue create `
    --repo $repo `
    --title "[TEST-6] Python OSC連携 制御イベント疎通確認" `
    --body $issue6_body `
    --label "test:hardware","test:prerequisite"
Write-Host "✅ Issue 6 created: Python OSC"

# ============================================================
# Issue 7: PositionLogger & 実機ビルド動作確認
# ============================================================
$issue7_body = @"
## 概要
PositionLogger の記録確認、および実機ビルドで確認すべき各種 UX 項目。

## テスト手順・確認項目

### 📍 PositionLogger
- [ ] ルーム接続後、1 秒毎に ``[Pos]`` 行が cogaze_*.log に出るか
- [ ] head（local）座標が記録されているか
- [ ] 各 PhotonView プレイヤー（name/role）が記録されているか
- [ ] SharedMesh / QR マーカー座標が記録されているか

### 🕹️ レイ（Laser Ray）
- [ ] 手/コントローラに追従するレイ（Laser）が **消えている** か
  - 残っている場合: Meta Building Block 由来の可能性 → 要調査

### 🏠 RemoteExpert レイアウト
- [ ] Expert 画面左側が隠れていないか（ExpertVideoDisplay の位置・サイズ確認）
  - 隠れている場合: ExpertVideoDisplay の RectTransform 要調整

## 判定基準
- 1 秒毎の ``[Pos]`` ログが確認できる → ✅ PASS
- レイが非表示 → ✅ PASS
- Expert 左側 UI が正しく表示される → ✅ PASS（or 別 issue 起票）
"@

gh issue create `
    --repo $repo `
    --title "[TEST-7] PositionLogger記録確認・実機UX確認" `
    --body $issue7_body `
    --label "test:hardware"
Write-Host "✅ Issue 7 created: Logging & UX"

# ============================================================
# Issue 8: 実験本番化 残実装タスク
# ============================================================
$issue8_body = @"
## 概要
実験本番化に向けた未実装・未対応タスクのトラッキング。

## 残タスク一覧

### 🎥 アセンブリ動画記録（大・未着手）
- [ ] 記録方式の決定: 画像連番 OR 動画エンコード
- [ ] 映像ソースの決定: Worker PCA 生フレーム OR Expert 受信 WebRTC
- [ ] トリガー設計: Assembly タスク開始/終了
- [ ] 保存先設計: ``logs/Pxx/`` 以下
- 実装は方針決定後

### 🔁 QR 再較正の有効化（小）
- [ ] ``MeshHandler._qrCalibrated`` のリセットを ``ClearAndReinitialize()`` 時に追加
- [ ] これにより QR スキャンで何度でも再較正可能になる

### 🎙️ VoiceRecorder ストリーミング化（保留）
- [ ] 現状: ``OnDestroy`` で一括保存（1 時間セッションでメモリ/クラッシュリスクあり）
- [ ] 対応: 逐次ディスク書き込みへ変更

### 📺 RemoteExpert 左側 UI 修正
- [ ] ``ExpertVideoDisplay`` の RectTransform 位置・サイズ調整
- [ ] Expert 画面で映像が左側に切れないことを確認

### 🧩 スタンドアロン化
- [ ] Inspector 依存フィールドの最終棚卸し
- [ ] Quest はヘッドレス動作できるか確認（PC 側で ``cogaze_config.json`` 事前書き込み）

### 📝 ログ削減（本番ビルド）
- [ ] ``Debug.Log`` を ``#if UNITY_EDITOR`` or FileLogger のみに絞る
- [ ] 本番 APK でのログ量を適切に調整

### 📋 実験タスク文面（人手確認）
- [ ] ``instructions_new.txt`` の ``[remote]``/``[local]`` 文面を実験者自身が確認
- [ ] AI 任せ不可（内容は実験設計に依存）

### 🔌 ExpertUI 側 ResetDualCalibration RPC（オプション）
- [ ] Expert 側から RPC で ``ResetDualCalibration()`` を呼べるように配線
- [ ] M キーでズレを確認した後、Expert 主導でリセットできる

## 優先順位
1. アセンブリ動画記録（実験データ収集に直結）
2. QR 再較正（小・1時間以内）
3. VoiceRecorder ストリーミング化（長時間セッション対策）
4. その他は実機テスト後に判断
"@

gh issue create `
    --repo $repo `
    --title "[IMPL-8] 実験本番化 残実装タスク一覧" `
    --body $issue8_body `
    --label "impl"
Write-Host "✅ Issue 8 created: Production Impl"

Write-Host "`n🎉 All 8 issues created on $repo"
