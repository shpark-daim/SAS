using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace sas.Tests;

public class SafetyMonitoringServiceTests {
    // ──────────────────────────────────────────────────────────
    // 공통 테스트 맵 (세그먼트 ID로 구분)
    // ──────────────────────────────────────────────────────────
    private static readonly DetectionMap HelmetA   = Map("CH_A", "HLM1", EventType.HelmetMissing,   [1, 2]);
    private static readonly DetectionMap IntrusA   = Map("CH_A", "ITR1", EventType.PersonIntrusion, [3, 4]);
    private static readonly DetectionMap HandA     = Map("CH_A", "CTT1", EventType.HandDetected,    [5]);
    private static readonly DetectionMap HelmetB   = Map("CH_B", "HLM1", EventType.HelmetMissing,   [6]);
    private static readonly DetectionMap IntrusB   = Map("CH_B", "ITR1", EventType.PersonIntrusion, [7]);

    private static DetectionMap Map(string ch, string roi, EventType type, int[] segs)
        => new(ch, roi, type, segs, [], []);

    // ──────────────────────────────────────────────────────────
    // 테스트 인프라
    // ──────────────────────────────────────────────────────────
    private record Sut(
        SafetyMonitoringService Service,
        IConnectionService Conn,
        IDetectionMapService Maps,
        CancellationTokenSource Cts
    ) : IAsyncDisposable {
        public async ValueTask DisposeAsync() {
            Cts.Cancel();
            await Service.StopAsync(CancellationToken.None);
            Service.Dispose();
            Cts.Dispose();
        }
    }

    private static Sut Create(int presetDwell = 1, int recoveryDwell = 1) {
        var conn     = Substitute.For<IConnectionService>();
        var maps     = Substitute.For<IDetectionMapService>();
        var manager  = Substitute.For<ISafetyMonitoringManager>();
        var vehicles = Substitute.For<IVehicleService>();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        var cts      = new CancellationTokenSource();

        // manager: 세그먼트를 그대로 반환 (제어 명령 검증을 conn에서 함)
        manager.OnDetected(Arg.Any<DetectionMap>())
            .Returns(ci => new DetectTargets([], [], ci.Arg<DetectionMap>().SegmentIds, []));
        manager.OnCleared(Arg.Any<DetectionMap>())
            .Returns(ci => new ResumeTargets([], [], ci.Arg<DetectionMap>().SegmentIds, []));

        // 기본값: 맵 없음
        maps.GetDetectionMaps(Arg.Any<string>(), Arg.Any<EventType>()).Returns([]);

        var svc = new SafetyMonitoringService(
            conn, maps, manager, vehicles, lifetime,
            Options.Create(new GeneralOptions {
                PresetDwellTime           = presetDwell,
                RecoveryDwellTime         = recoveryDwell,
                TimeToKillProgramAfterRecovery = 0,
                ReceivableRoi             = false,
            }),
            Substitute.For<ILogger<SafetyMonitoringService>>()
        );
        svc.StartAsync(cts.Token);

        return new Sut(svc, conn, maps, cts);
    }

    private static sas.ScpStatus Evt(string ch, EventType type, sas.Status status)
        => new("1", "test", Guid.NewGuid().ToString(), "SITE", ch, type, status, DateTimeOffset.UtcNow);

    /// <summary>이벤트 전송 후 이벤트 루프가 처리할 때까지 대기</summary>
    private static async Task Send(Sut sut, sas.ScpStatus s, int waitMs = 200) {
        await sut.Service.WriteChannelAsync(s);
        await Task.Delay(waitMs);
    }

    // ──────────────────────────────────────────────────────────
    // H - HandDetected (preset 무관, 즉시)
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task H1_HandDetected_New_즉시_ActivateMap() {
        await using var sut = Create();
        sut.Maps.GetDetectionMaps("CH_A", EventType.HandDetected).Returns([HandA]);

        await Send(sut, Evt("CH_A", EventType.HandDetected, sas.Status.NEW));

        await sut.Conn.Received(1).ModifySegment(5, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task H2_HandDetected_New_Finished_RecoveryDwell0_즉시_ExecuteFinished() {
        await using var sut = Create(recoveryDwell: 0);
        sut.Maps.GetDetectionMaps("CH_A", EventType.HandDetected).Returns([HandA]);

        await Send(sut, Evt("CH_A", EventType.HandDetected, sas.Status.NEW));
        await Send(sut, Evt("CH_A", EventType.HandDetected, sas.Status.FINISHED));

        await sut.Conn.Received(1).ModifySegment(5, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task H3_HandDetected_New_Finished_RecoveryDwell1_1초전에는미실행_이후실행() {
        await using var sut = Create(recoveryDwell: 1);
        sut.Maps.GetDetectionMaps("CH_A", EventType.HandDetected).Returns([HandA]);

        await Send(sut, Evt("CH_A", EventType.HandDetected, sas.Status.NEW));
        await Send(sut, Evt("CH_A", EventType.HandDetected, sas.Status.FINISHED));

        // RecoveryDwellTime 전: 실행 안 됨
        await sut.Conn.DidNotReceive().ModifySegment(5, false, Arg.Any<CancellationToken>());

        // RecoveryDwellTime 이후: 실행
        await Task.Delay(1200);
        await sut.Conn.Received(1).ModifySegment(5, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task H4_HandDetected_RecoveryDwell중_New도착_RecoveryDwell취소_재활성() {
        await using var sut = Create(recoveryDwell: 1);
        sut.Maps.GetDetectionMaps("CH_A", EventType.HandDetected).Returns([HandA]);

        await Send(sut, Evt("CH_A", EventType.HandDetected, sas.Status.NEW));
        await Send(sut, Evt("CH_A", EventType.HandDetected, sas.Status.FINISHED));
        await Send(sut, Evt("CH_A", EventType.HandDetected, sas.Status.NEW)); // RecoveryDwell 취소

        // 두 번 활성화 (첫 번째 NEW, 두 번째 NEW)
        await sut.Conn.Received(2).ModifySegment(5, true, Arg.Any<CancellationToken>());
        // 취소됐으므로 ExecuteFinished 없음
        await sut.Conn.DidNotReceive().ModifySegment(5, false, Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────
    // P - Preset 페어링 (HelmetMissing ↔ PersonIntrusion)
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task P1_HelmetNew_IntrusionNew_페어성립_둘다ActivateMap() {
        await using var sut = Create(presetDwell: 1);
        sut.Maps.GetDetectionMaps("CH_A", EventType.HelmetMissing).Returns([HelmetA]);
        sut.Maps.GetDetectionMaps("CH_A", EventType.PersonIntrusion).Returns([IntrusA]);

        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.NEW));
        // 페어 미성립 → 아무것도 안 함
        await sut.Conn.DidNotReceive().ModifySegment(Arg.Any<int>(), true, Arg.Any<CancellationToken>());

        await Send(sut, Evt("CH_A", EventType.PersonIntrusion, sas.Status.NEW));
        // 페어 성립 → Helmet 세그먼트(1,2) + Intrusion 세그먼트(3,4) 모두 활성화
        await sut.Conn.Received().ModifySegment(1, true, Arg.Any<CancellationToken>());
        await sut.Conn.Received().ModifySegment(2, true, Arg.Any<CancellationToken>());
        await sut.Conn.Received().ModifySegment(3, true, Arg.Any<CancellationToken>());
        await sut.Conn.Received().ModifySegment(4, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task P2_IntrusionNew_HelmetNew_순서무관_페어성립() {
        await using var sut = Create(presetDwell: 1);
        sut.Maps.GetDetectionMaps("CH_A", EventType.HelmetMissing).Returns([HelmetA]);
        sut.Maps.GetDetectionMaps("CH_A", EventType.PersonIntrusion).Returns([IntrusA]);

        await Send(sut, Evt("CH_A", EventType.PersonIntrusion, sas.Status.NEW));
        await sut.Conn.DidNotReceive().ModifySegment(Arg.Any<int>(), true, Arg.Any<CancellationToken>());

        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.NEW));
        await sut.Conn.Received().ModifySegment(1, true, Arg.Any<CancellationToken>());
        await sut.Conn.Received().ModifySegment(3, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task P3_HelmetNew_PresetDwellTime만료_아무동작없음() {
        await using var sut = Create(presetDwell: 1);
        sut.Maps.GetDetectionMaps("CH_A", EventType.HelmetMissing).Returns([HelmetA]);

        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.NEW));
        await Task.Delay(1500); // preset 타임아웃 대기

        await sut.Conn.DidNotReceive().ModifySegment(Arg.Any<int>(), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task P4_HelmetNew_HelmetNew_타이머리셋_첫타이머기준으로페어안됨() {
        await using var sut = Create(presetDwell: 1);
        sut.Maps.GetDetectionMaps("CH_A", EventType.HelmetMissing).Returns([HelmetA]);

        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.NEW));
        await Task.Delay(500);
        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.NEW)); // 리셋
        // 리셋 후 700ms → 첫 번째 기준 1.2s 경과했지만, 리셋된 타이머는 아직 안 만료
        await Task.Delay(700);

        await sut.Conn.DidNotReceive().ModifySegment(Arg.Any<int>(), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task P5_HelmetNew_HelmetNew리셋후_IntrusionNew_리셋된타이머기준_페어성립() {
        await using var sut = Create(presetDwell: 1);
        sut.Maps.GetDetectionMaps("CH_A", EventType.HelmetMissing).Returns([HelmetA]);
        sut.Maps.GetDetectionMaps("CH_A", EventType.PersonIntrusion).Returns([IntrusA]);

        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.NEW));
        await Task.Delay(500);
        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.NEW)); // 리셋
        await Send(sut, Evt("CH_A", EventType.PersonIntrusion, sas.Status.NEW)); // 리셋 후 즉시 페어

        // 리셋 후 도착한 HelmetMissing maps + IntrusionA maps 모두 활성화
        await sut.Conn.Received().ModifySegment(1, true, Arg.Any<CancellationToken>());
        await sut.Conn.Received().ModifySegment(3, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task P6_HelmetNew_HelmetFinished_프리셋취소_아무동작없음() {
        await using var sut = Create(presetDwell: 1, recoveryDwell: 1);
        sut.Maps.GetDetectionMaps("CH_A", EventType.HelmetMissing).Returns([HelmetA]);

        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.NEW));
        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.FINISHED));

        // ActivateMap 안 됨
        await sut.Conn.DidNotReceive().ModifySegment(Arg.Any<int>(), true, Arg.Any<CancellationToken>());
        // RecoveryDwell도 시작 안 됐으므로 ExecuteFinished도 없음
        await Task.Delay(1500);
        await sut.Conn.DidNotReceive().ModifySegment(Arg.Any<int>(), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task P7_HelmetNew_프리셋만료후_HelmetFinished_ActivatedKey없음_스킵() {
        await using var sut = Create(presetDwell: 1, recoveryDwell: 1);
        sut.Maps.GetDetectionMaps("CH_A", EventType.HelmetMissing).Returns([HelmetA]);

        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.NEW));
        await Task.Delay(1300); // preset 만료
        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.FINISHED));

        // _activatedKeys에 없으므로 ExecuteFinished 스킵
        await Task.Delay(1500);
        await sut.Conn.DidNotReceive().ModifySegment(Arg.Any<int>(), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task P8_HelmetNew_프리셋만료후_IntrusionNew_새_프리셋시작_페어없음() {
        await using var sut = Create(presetDwell: 1);
        sut.Maps.GetDetectionMaps("CH_A", EventType.HelmetMissing).Returns([HelmetA]);
        sut.Maps.GetDetectionMaps("CH_A", EventType.PersonIntrusion).Returns([IntrusA]);

        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.NEW));
        await Task.Delay(1300); // Helmet preset 만료

        await Send(sut, Evt("CH_A", EventType.PersonIntrusion, sas.Status.NEW)); // 새 preset 시작

        // Intrusion 혼자 대기 중 → ActivateMap 없음
        await sut.Conn.DidNotReceive().ModifySegment(Arg.Any<int>(), true, Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────
    // F - Activated 상태에서 Finished 처리
    // ──────────────────────────────────────────────────────────

    /// <summary>Helmet + Intrusion 페어 활성화 후 conn 호출 초기화</summary>
    private static async Task ActivatePair(Sut sut) {
        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.NEW));
        await Send(sut, Evt("CH_A", EventType.PersonIntrusion, sas.Status.NEW));
        sut.Conn.ClearReceivedCalls();
    }

    [Fact]
    public async Task F1_Activated_Finished_RecoveryDwell0_즉시_ExecuteFinished() {
        await using var sut = Create(presetDwell: 1, recoveryDwell: 0);
        sut.Maps.GetDetectionMaps("CH_A", EventType.HelmetMissing).Returns([HelmetA]);
        sut.Maps.GetDetectionMaps("CH_A", EventType.PersonIntrusion).Returns([IntrusA]);
        await ActivatePair(sut);

        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.FINISHED));

        await sut.Conn.Received().ModifySegment(1, false, Arg.Any<CancellationToken>());
        await sut.Conn.Received().ModifySegment(2, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task F2_Activated_Finished_RecoveryDwell1_1초전에는미실행_이후실행() {
        await using var sut = Create(presetDwell: 1, recoveryDwell: 1);
        sut.Maps.GetDetectionMaps("CH_A", EventType.HelmetMissing).Returns([HelmetA]);
        sut.Maps.GetDetectionMaps("CH_A", EventType.PersonIntrusion).Returns([IntrusA]);
        await ActivatePair(sut);

        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.FINISHED));

        await sut.Conn.DidNotReceive().ModifySegment(Arg.Any<int>(), false, Arg.Any<CancellationToken>());
        await Task.Delay(1300);
        await sut.Conn.Received().ModifySegment(1, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task F3_Activated_연속Finished_첫번째타이머유지_1번만ExecuteFinished() {
        await using var sut = Create(presetDwell: 1, recoveryDwell: 1);
        sut.Maps.GetDetectionMaps("CH_A", EventType.HelmetMissing).Returns([HelmetA]);
        sut.Maps.GetDetectionMaps("CH_A", EventType.PersonIntrusion).Returns([IntrusA]);
        await ActivatePair(sut);

        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.FINISHED));
        await Task.Delay(300);
        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.FINISHED)); // 두 번째 (무시)

        await Task.Delay(1500); // 충분히 대기
        // 세그먼트당 정확히 1번만 비활성화
        await sut.Conn.Received(1).ModifySegment(1, false, Arg.Any<CancellationToken>());
        await sut.Conn.Received(1).ModifySegment(2, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task F4_Activated_RecoveryDwell중_HelmetNew단독_CancelPendingFinish없음_RecoveryDwell유지() {
        await using var sut = Create(presetDwell: 1, recoveryDwell: 1);
        sut.Maps.GetDetectionMaps("CH_A", EventType.HelmetMissing).Returns([HelmetA]);
        sut.Maps.GetDetectionMaps("CH_A", EventType.PersonIntrusion).Returns([IntrusA]);
        await ActivatePair(sut);

        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.FINISHED));
        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.NEW)); // 단독 → preset 대기, RecoveryDwell 계속

        // 단독 Helmet NEW는 ActivateMap 안 함
        await sut.Conn.DidNotReceive().ModifySegment(Arg.Any<int>(), true, Arg.Any<CancellationToken>());
        // RecoveryDwell 만료 → ExecuteFinished 실행
        await Task.Delay(1300);
        await sut.Conn.Received().ModifySegment(1, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task F5_Activated_RecoveryDwell중_페어New_RecoveryDwell취소_재활성() {
        await using var sut = Create(presetDwell: 1, recoveryDwell: 1);
        sut.Maps.GetDetectionMaps("CH_A", EventType.HelmetMissing).Returns([HelmetA]);
        sut.Maps.GetDetectionMaps("CH_A", EventType.PersonIntrusion).Returns([IntrusA]);
        await ActivatePair(sut);

        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.FINISHED));
        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.NEW));    // preset 대기
        await Send(sut, Evt("CH_A", EventType.PersonIntrusion, sas.Status.NEW));  // 페어 → CancelPendingFinish → 재활성

        // 재활성화 확인
        await sut.Conn.Received().ModifySegment(1, true, Arg.Any<CancellationToken>());
        // RecoveryDwell 취소됐으므로 충분히 기다려도 ExecuteFinished 없음
        await Task.Delay(1500);
        await sut.Conn.DidNotReceive().ModifySegment(1, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task F6_Activated_RecoveryDwell중_HelmetNew_프리셋만료_RecoveryDwell정상실행() {
        await using var sut = Create(presetDwell: 1, recoveryDwell: 2);
        sut.Maps.GetDetectionMaps("CH_A", EventType.HelmetMissing).Returns([HelmetA]);
        sut.Maps.GetDetectionMaps("CH_A", EventType.PersonIntrusion).Returns([IntrusA]);
        await ActivatePair(sut);

        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.FINISHED));
        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.NEW)); // preset 대기 시작
        await Task.Delay(1300); // preset 만료 (pair 없음), RecoveryDwell(2s)은 계속

        await sut.Conn.DidNotReceive().ModifySegment(Arg.Any<int>(), false, Arg.Any<CancellationToken>());
        await Task.Delay(1000); // 총 2초 이상 → RecoveryDwell 만료
        await sut.Conn.Received().ModifySegment(1, false, Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────
    // C - 채널 분리 (ChannelId 단위 페어링)
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task C1_다른채널_HelmetNew_IntrusionNew_페어불성립() {
        await using var sut = Create(presetDwell: 1);
        sut.Maps.GetDetectionMaps("CH_A", EventType.HelmetMissing).Returns([HelmetA]);
        sut.Maps.GetDetectionMaps("CH_B", EventType.PersonIntrusion).Returns([IntrusB]);

        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.NEW));
        await Send(sut, Evt("CH_B", EventType.PersonIntrusion, sas.Status.NEW));

        // 채널이 다르므로 둘 다 각자 preset 대기 → ActivateMap 없음
        await sut.Conn.DidNotReceive().ModifySegment(Arg.Any<int>(), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task C2_같은채널_HelmetNew_IntrusionNew_페어성립() {
        await using var sut = Create(presetDwell: 1);
        sut.Maps.GetDetectionMaps("CH_A", EventType.HelmetMissing).Returns([HelmetA]);
        sut.Maps.GetDetectionMaps("CH_A", EventType.PersonIntrusion).Returns([IntrusA]);

        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.NEW));
        await Send(sut, Evt("CH_A", EventType.PersonIntrusion, sas.Status.NEW));

        await sut.Conn.Received().ModifySegment(1, true, Arg.Any<CancellationToken>());
        await sut.Conn.Received().ModifySegment(3, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task C3_두채널_독립_각각페어성립() {
        await using var sut = Create(presetDwell: 1);
        sut.Maps.GetDetectionMaps("CH_A", EventType.HelmetMissing).Returns([HelmetA]);
        sut.Maps.GetDetectionMaps("CH_A", EventType.PersonIntrusion).Returns([IntrusA]);
        sut.Maps.GetDetectionMaps("CH_B", EventType.HelmetMissing).Returns([HelmetB]);
        sut.Maps.GetDetectionMaps("CH_B", EventType.PersonIntrusion).Returns([IntrusB]);

        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.NEW));
        await Send(sut, Evt("CH_B", EventType.HelmetMissing, sas.Status.NEW));
        await Send(sut, Evt("CH_A", EventType.PersonIntrusion, sas.Status.NEW));
        await Send(sut, Evt("CH_B", EventType.PersonIntrusion, sas.Status.NEW));

        // 두 채널 모두 독립적으로 페어 성립
        await sut.Conn.Received().ModifySegment(1, true, Arg.Any<CancellationToken>()); // CH_A Helmet
        await sut.Conn.Received().ModifySegment(3, true, Arg.Any<CancellationToken>()); // CH_A Intrusion
        await sut.Conn.Received().ModifySegment(6, true, Arg.Any<CancellationToken>()); // CH_B Helmet
        await sut.Conn.Received().ModifySegment(7, true, Arg.Any<CancellationToken>()); // CH_B Intrusion
    }

    // ──────────────────────────────────────────────────────────
    // D - PresetDwellTime = 0 (preset 비활성)
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task D1_PresetDwell0_HelmetNew_즉시_ActivateMap() {
        await using var sut = Create(presetDwell: 0);
        sut.Maps.GetDetectionMaps("CH_A", EventType.HelmetMissing).Returns([HelmetA]);

        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.NEW));

        await sut.Conn.Received().ModifySegment(1, true, Arg.Any<CancellationToken>());
        await sut.Conn.Received().ModifySegment(2, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task D2_PresetDwell0_IntrusionNew_즉시_ActivateMap() {
        await using var sut = Create(presetDwell: 0);
        sut.Maps.GetDetectionMaps("CH_A", EventType.PersonIntrusion).Returns([IntrusA]);

        await Send(sut, Evt("CH_A", EventType.PersonIntrusion, sas.Status.NEW));

        await sut.Conn.Received().ModifySegment(3, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task D3_PresetDwell0_Activated_Finished_정상_RecoveryDwell진행() {
        await using var sut = Create(presetDwell: 0, recoveryDwell: 1);
        sut.Maps.GetDetectionMaps("CH_A", EventType.HelmetMissing).Returns([HelmetA]);

        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.NEW));
        await Send(sut, Evt("CH_A", EventType.HelmetMissing, sas.Status.FINISHED));

        await sut.Conn.DidNotReceive().ModifySegment(Arg.Any<int>(), false, Arg.Any<CancellationToken>());
        await Task.Delay(1300);
        await sut.Conn.Received().ModifySegment(1, false, Arg.Any<CancellationToken>());
    }
}
