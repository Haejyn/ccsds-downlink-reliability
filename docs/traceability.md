# 요구사항 추적 매트릭스 (자동 생성)

> `python tools/trace.py` 가 `docs/requirements.md` · 시험 코드의 `[Trait("Requirement", …)]` · TRX 결과로 만든다. 손으로 고치지 않는다.

| 요구사항 | 내용 | 시험 | 실행 | 판정 |
|---|---|---|---|---|
| REQ-CRC-01 | FECF 는 CRC-16/CCITT-FALSE 로 계산한다 | `CrcTests#Check_value_of_standard_test_string_is_0x29B1`<br>`CrcTests#Table_driven_crc_matches_bitwise_reference_on_random_buffers` | 2/2 | ✅ 통과 |
| REQ-FRM-01 | 전송 프레임 주 헤더를 표준 비트 배치대로 부호화·복호화한다 | `TransferFrameTests#Encoding_follows_the_ccsds_bit_layout`<br>`TransferFrameTests#Random_frames_round_trip_with_and_without_ocf`<br>`TransferFrameTests#Frames_without_frame_error_control_round_trip` | 3/3 | ✅ 통과 |
| REQ-FRM-02 | 채널 비트 오류가 있는 프레임은 받아들이지 않는다 | `FrameErrorDetectionTests#Every_single_bit_error_in_a_1115_byte_frame_is_detected`<br>`FrameErrorDetectionTests#Random_multi_bit_errors_are_detected`<br>`FrameErrorDetectionTests#Every_burst_error_up_to_16_bits_is_detected` | 5/5 | ✅ 통과 |
| REQ-FRM-03 | 구조가 잘못된 프레임은 CRC 가 맞아도 이유와 함께 거부한다 | `TransferFrameTests#Structurally_invalid_frames_are_rejected_with_a_reason_even_when_crc_is_valid`<br>`TransferFrameTests#Invalid_construction_is_refused`<br>`TransferFrameTests#Data_field_length_limits_are_inclusive_at_both_ends` | 3/3 | ✅ 통과 |
| REQ-PKT-01 | Space Packet 주 헤더를 표준 비트 배치대로 부호화·복호화한다 | `SpacePacketTests#Encoding_follows_the_ccsds_bit_layout`<br>`SpacePacketTests#Random_packets_round_trip`<br>`SpacePacketTests#Value_equality_distinguishes_packets_by_content` | 3/3 | ✅ 통과 |
| REQ-PKT-02 | 필드 범위 밖·형식 오류 패킷은 거부한다 | `SpacePacketTests#Field_boundaries_are_enforced`<br>`SpacePacketTests#Malformed_packets_are_rejected` | 2/2 | ✅ 통과 |
| REQ-EXT-01 | 여러 프레임에 걸친 패킷·프레임 경계에서 쪼개진 헤더·유휴 프레임이 있어도 패킷을 순서대로 정확히 재조립한다 | `NominalReceptionTests#Packets_of_any_length_are_reassembled_exactly_and_in_order`<br>`NominalReceptionTests#Packet_header_split_across_a_frame_boundary_is_reassembled`<br>`NominalReceptionTests#Idle_frames_between_bursts_do_not_disturb_reassembly`<br>`FaultInjectionTests#Nominal_reception_with_packet_error_control_matches_input` | 4/4 | ✅ 통과 |
| REQ-EXT-02 | 가상 채널이 섞여 들어와도 채널별로 독립 재조립한다 | `NominalReceptionTests#Virtual_channels_are_reassembled_independently_when_interleaved` | 1/1 | ✅ 통과 |
| REQ-EXT-03 | 프레임 유실·비트 오류·순서 뒤바뀜에서 손상 패킷 0, 영향받지 않은 패킷은 전부 복구 | `FaultInjectionTests#Frame_gap_event_reports_how_many_frames_were_lost`<br>`FaultInjectionTests#Dropped_frames_lose_only_the_packets_they_carried`<br>`FaultInjectionTests#Bit_errors_are_caught_by_crc_and_behave_like_lost_frames`<br>`FaultInjectionTests#Reordered_frames_never_produce_a_corrupted_packet` | 7/7 | ✅ 통과 |
| REQ-EXT-04 | 중복 수신 프레임은 무시한다 | `FaultInjectionTests#Duplicated_frames_are_ignored_without_duplicating_packets` | 1/1 | ✅ 통과 |
| REQ-EXT-05 | APID 별 패킷 순서 카운트 건너뜀을 보고한다 | `NominalReceptionTests#Packet_sequence_count_gaps_are_reported_per_apid` | 1/1 | ✅ 통과 |
| REQ-EXT-06 | 프레임 카운트로 감지되지 않는 유실(256 장 단위 연속 유실)에서도 손상 패킷을 내보내지 않는다 | `SpacePacketTests#Error_control_check_is_false_when_the_packet_cannot_hold_a_crc`<br>`FaultInjectionTests#Defect_C1_without_packet_error_control_undetectable_frame_loss_emits_corrupted_packets`<br>`FaultInjectionTests#With_packet_error_control_undetectable_frame_loss_never_emits_a_corrupted_packet`<br>`FaultInjectionTests#Packet_error_control_rejects_a_packet_with_any_single_bit_error` | 8/8 | ✅ 통과 |
| REQ-EXT-07 | 어떤 입력 바이트열에도 예외로 중단되지 않는다 | `RobustnessTests#Arbitrary_bytes_never_crash_the_receiver`<br>`RobustnessTests#Frames_with_valid_crc_but_random_content_never_crash_the_receiver` | 2/2 | ✅ 통과 |
| REQ-EXT-08 | 실시간 수신 처리량 | `RobustnessTests#Throughput_is_measured_and_does_not_regress_below_5k_frames_per_second` | 1/1 | ✅ 통과 |
| REQ-EXT-09 | 패킷 버전 번호가 0 이 아니면 동기를 잃은 것으로 보고, 다음 제1 헤더 포인터까지 그 가상 채널의 조립 데이터를 버린다 | `FaultInjectionTests#Invalid_packet_version_desynchronizes_until_the_next_first_header_pointer` | 1/1 | ✅ 통과 |

**요구사항 15개 중 15개 검증됨.**
