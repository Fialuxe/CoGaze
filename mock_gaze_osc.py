import time
import pyautogui
from pythonosc import udp_client

# 設定
IP = "127.0.0.1"
PORT = 8000  # Unity側（Tobii仕様）に合わせました
ADDRESS = "/gaze"

def main():
    # OSCクライアントのセットアップ
    client = udp_client.SimpleUDPClient(IP, PORT)
    print(f"OSCクライアントを起動しました。送信先: {IP}:{PORT} (アドレス: {ADDRESS})")
    print("終了するには Ctrl+C を押してください。")

    # 画面の解像度を取得
    screen_width, screen_height = pyautogui.size()
    print(f"画面解像度: {screen_width}x{screen_height}")

    try:
        while True:
            # マウスの現在座標を取得 (左上が0,0, 右下がwidth,height)
            x, y = pyautogui.position()

            # 0.0 ～ 1.0 に正規化
            # Tobiiアイトラッカーと同じ仕様（左上が0,0）のまま送信します
            norm_x = x / screen_width
            norm_y = y / screen_height

            # まばたき（とりあえず今回は0.0固定）
            blink = 0.0

            # OSCメッセージを送信 [x, y, blink]
            client.send_message(ADDRESS, [float(norm_x), float(norm_y), float(blink)])

            # デバッグ出力（ターミナルが埋まらないように少し待機）
            # print(f"Sent: X={norm_x:.3f}, Y={norm_y:.3f}, Blink={blink}")
            
            # 約60FPSで送信
            time.sleep(1.0 / 60.0)

    except KeyboardInterrupt:
        print("\n送信を終了しました。")

if __name__ == "__main__":
    main()
