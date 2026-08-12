// ThrowMe 릴레이 프로토콜 — 클라이언트/서버 공용 메시지 정의.
// 설계문서 `ThrowMe_멀티PC_인터넷_릴레이_설계.md` 9절과 일치.
//
// 모든 메시지는 봉투(Envelope)로 감싸 서버가 라우팅한다.
// 좌표는 절대값을 보내지 않고 엣지 파라미터 t + 엣지 기준 속도만 담는다(해상도/DPI 무관).

export const PROTOCOL_VERSION = 1;

export type MessageType =
  | "HELLO"          // client → server : 방 참여(코드+시크릿) 인증
  | "WELCOME"        // server → client : 인증 성공(내 nodeId·현재 owner·링크)
  | "PRESENCE"       // server → 방 전체 : 온라인 노드 목록 + owner
  | "ROOM_CONFIG"    // client → server(설정) / server → 방 전체(배포) : 엣지 매핑
  | "SET_ORDER"      // host → server : 파티 순서(좌→우 배치) 변경. 방장만 허용
  | "TRANSFER_HOST"  // host → server : 방장 위임(대상 노드로). 방장만 허용
  | "SET_THEME"      // host → server : 방 공통 테마 지정. 방장만 허용
  | "ROOM_STYLE"     // host → server → 방 전체 : 방장의 겉모습 전체(테마·가중치·그림). 방장만 허용
  | "HANDOFF"        // owner → server → target : 공 넘김
  | "ACK"            // target → server : 공 수락
  | "HANDOFF_RESULT" // server → origin : 최종 결과(accepted → 해제 / 거부 → 반사)
  | "HEARTBEAT"      // client → server : 생존 확인
  | "ERROR";         // server → client : 오류 고지

export interface Envelope<T = unknown> {
  v: number;               // 프로토콜 버전
  type: MessageType;
  roomId?: string;
  from?: string;           // 보낸 노드 id
  to?: string;             // 서버 라우팅 대상 노드 id (없으면 브로드캐스트)
  seq?: number;            // 순서/중복 방지
  sig?: string;            // (예약) 메시지 서명 — Phase 7-G 강화 항목
  data?: T;
}

// ── 타입별 페이로드 ────────────────────────────────────────────

export interface HelloData {
  secret: string;          // 방 시크릿(최초 참여 시 방 생성·시크릿 등록)
  version: string;         // 클라이언트 버전(HELLO 신원 교환)
}

export interface WelcomeData {
  nodeId: string;          // 서버가 확정한 내 노드 id
  owner: string | null;    // 현재 공 소유 노드
  links: EdgeLink[];       // 엣지 매핑
  nodes: NodePresence[];   // 현재 온라인 노드
  host: string | null;     // 방장(방을 처음 만든 노드). 배치 결정 권한
  order: string[];         // 파티 순서(좌 → 우). 방장이 정한다
  theme: string | null;    // 방 공통 테마(방장이 지정). null = 각자 설정 유지
}

/** 파티 순서 변경(방장만). */
export interface SetOrderData {
  order: string[];         // 좌 → 우 순서의 nodeId 목록
}

/** 방장 위임(방장만). */
export interface TransferHostData {
  to: string;              // 새 방장이 될 nodeId(접속 중이어야 함)
}

/** 방 공통 테마 지정(방장만). */
export interface SetThemeData {
  theme: string;           // AppSettings.Skin 이름(Jelly/Billiard/Pokeball/...)
}

/**
 * 방장의 겉모습을 방 전체에 그대로 적용(방장만).
 *
 * 이미지(PNG base64)가 들어갈 수 있어 **서버는 저장하지 않고 중계만** 한다.
 * (DO 저장은 키당 128KB 제한이라 이미지를 담기 어렵다.)
 * 나중에 들어온 사람에게는 방장이 프레즌스 변화를 보고 다시 보내 준다.
 */
export interface RoomStyleData {
  skin: string;              // 테마
  throwPower: number;        // 던지기 가중치
  restitution: number;       // 반발력
  softness: number;          // 말랑함
  slimeSize: number;         // 크기
  skinImageEnabled: boolean; // 커스텀 이미지 덧씌우기
  skinImageScale: number;    // 이미지 크기 비율
  imageSkin?: string;        // 이미지가 속한 테마 이름
  imagePng?: string;         // 그림판/불러온 이미지(base64 PNG). 없으면 이미지 없음
}

export interface NodePresence {
  nodeId: string;
  online: boolean;
  hasBall: boolean;
}

export interface PresenceData {
  nodes: NodePresence[];
  owner: string | null;
  host: string | null;     // 현재 방장(이탈 시 다음 노드로 승계)
  order: string[];         // 파티 순서(좌 → 우)
  theme: string | null;    // 방 공통 테마
}

export type Edge = "Left" | "Right" | "Top" | "Bottom";

export interface EdgeLink {
  from: string;            // nodeId
  fromEdge: Edge;
  to: string;              // nodeId
  toEdge: Edge;
  flip: boolean;           // true면 진입 t → 1-t, 접선 부호 반전(거울)
}

export interface RoomConfigData {
  links: EdgeLink[];
}

// 설계문서 9절 HANDOFF.data — LAN 설계 6절 필드 그대로 재사용.
export interface HandoffData {
  handoffId: string;       // ACK 매칭용
  viaLink: string;         // "A.Right->B.Left" 링크 식별
  edgeParam: number;       // 진입 엣지 t (0~1)
  normalSpeed: number;     // 엣지 법선 성분(px/s, 항상 양수=안쪽)
  tangentSpeed: number;    // 접선 성분(부호=방향)
  angularVelocity: number; // deg/s
  surfaceSpin: number;     // px/s (끌어치기/밀어치기)
  surfaceSpinAxisDeg: number; // SpinShotDir 각도(엣지 기준)
  spinAngle: number;       // 시각 회전 연속성
}

export interface AckData {
  handoffId: string;
  accepted: boolean;
}

export interface HandoffResultData {
  handoffId: string;
  accepted: boolean;       // true=상대가 받음(공 해제) / false=실패(반사로 회수)
  reason?: string;
}

export interface ErrorData {
  code: string;
  message: string;
}

// ── 오류 코드 ─────────────────────────────────────────────────
export const ErrorCodes = {
  BAD_MESSAGE: "BAD_MESSAGE",
  NOT_AUTHENTICATED: "NOT_AUTHENTICATED",
  BAD_SECRET: "BAD_SECRET",
  ROOM_FULL: "ROOM_FULL",
  NOT_OWNER: "NOT_OWNER",
  NOT_HOST: "NOT_HOST",
  TARGET_NOT_FOUND: "TARGET_NOT_FOUND",
  TARGET_OFFLINE: "TARGET_OFFLINE",
  VERSION_MISMATCH: "VERSION_MISMATCH",
} as const;

// 방 한도(남용 방지). 개인/소규모용 기본값.
export const MAX_NODES_PER_ROOM = 16;

// 핸드오프 ACK 타임아웃(ms). 인터넷 지연 고려. 초과 시 owner 롤백 → origin이 반사.
export const HANDOFF_TIMEOUT_MS = 1500;

// 하트비트 없이 이 시간(ms) 지나면 죽은 연결로 간주(참고용).
export const HEARTBEAT_TIMEOUT_MS = 45_000;

// 모두 나가고 이 시간(ms) 동안 아무도 안 들어오면 방을 폐기한다(시크릿·순서·배치 전부 삭제).
// 잠깐 껐다 켜는 경우(앱 재시작·네트워크 끊김)에는 방이 유지되도록 넉넉히 잡는다.
export const EMPTY_ROOM_TTL_MS = 30 * 60 * 1000; // 30분
