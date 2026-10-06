# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/)
and this project adheres to [Semantic Versioning](http://semver.org/).

## [Unreleased]

### Added

- Rendererを持つGameObjectの右クリックから、そのRendererだけを通常・VRプレビューに表示してマテリアルをスロット別に比較・編集する機能を追加。
- Rendererのスロットに適用されるAOME／TTT MaterialModifierをアバター配下から検出し、既存設定の編集へ切り替える機能を追加。複数候補・空設定・無効設定・AOMEのスロット除外に対応し、設定全体への影響と切り替え時の未保存確認を表示。
- MLIC（MultiLayerImageCanvas）の合成画像を通常・VRプレビューとプロパティ比較に反映。対象のTexture・Scale・Offsetを読み取り専用とし、保存時の検査、MLIC入力アセットの変更検出、一時画像の解放に対応。

### Fixed

- TTT MaterialModifier／AOME選択時の差分表示を、選択設定の適用直前のOverride元を基準に変更。追加編集なしで既存設定の差分を表示し、未保存判定は編集開始時点からの変更を維持。
- 自身または親にEditorOnlyタグが付いたRendererを、マテリアル一覧と通常・VRプレビューの対象から除外。
- 通常UIで変更のないマテリアルやアバター設定を繰り返し全件検査していた処理を変更検知で抑制し、UI用の差分結果を再利用して操作時の負荷を軽減。保存前の完全検証と3Dプレビューの姿勢追従は維持。
- アバター内の衣装など、階層にスケールがあるSkinnedMeshRendererの通常プレビューでスケールが二重適用される問題を修正。

## [0.1.0] - 2026-10-05

### Added

- 新規作成
