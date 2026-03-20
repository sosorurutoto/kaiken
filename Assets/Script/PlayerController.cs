using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerController : MonoBehaviour
{
    public enum PlayerState
    {
        Idle,
        Walk,
        Dash,
        DashRecovery,
        Attack,
        Guard
    }

    [Header("Move Settings")]
    [SerializeField] private float walkSpeed = 3f;
    [SerializeField] private float dashSpeed = 12f;
    [SerializeField] private float dashDuration = 0.15f;
    [SerializeField] private float dashRecoveryTime = 0.2f;
    [SerializeField] private float invincibilityDuration = 0.05f;//無敵時間
    [SerializeField] private float dashCooldown = 0.5f;
    [SerializeField] private float inputDeadZone = 0.2f;//入力無効時間

    [Header("Input Actions (New Input System)")]
    [SerializeField] private InputActionReference moveAction;
    [SerializeField] private InputActionReference dashAction;

    [Header("Animator")]
    [SerializeField] private Animator animator;

    [Header("Axis Lock")]
    [SerializeField] private bool lockYPosition = true;
    [SerializeField] private bool lockZPosition = true;

    public PlayerState CurrentState => currentState;//読み取り専用プロパティを作成、値の取得と内部制御を明確に分離する。読み取り専用のため内部状態を外部から直接値を変更されず安全性が高まる
    public bool IsInvincible => isInvincible;//読み取り専用プロパティ
    public bool IsFacingRight => isFacingRight;//読み取り専用プロパティ
    public bool CanDash =>
        cooldownTimer <= 0f &&
        currentState != PlayerState.Dash &&
        currentState != PlayerState.DashRecovery &&
        currentState != PlayerState.Attack &&
        currentState != PlayerState.Guard;

    private PlayerState currentState = PlayerState.Idle;

    private bool isInvincible = false;
    private bool isFacingRight = true;

    private float dashTimer = 0f;
    private float recoveryTimer = 0f;
    private float invincibilityTimer = 0f;
    private float cooldownTimer = 0f;

    private int dashDirection = 0;

    private float initialY;
    private float initialZ;
    private float defaultScaleX = 1f;

    private void Awake()
    {
        initialY = transform.position.y;
        initialZ = transform.position.z;

        defaultScaleX = Mathf.Abs(transform.localScale.x);//通常状態のxのScaleの値を取得してそれを絶対値として向きの反転の基準値とする
        if (defaultScaleX <= 0f)
        {
            defaultScaleX = 1f;
        }

        if (animator == null)
        {
            animator = GetComponentInChildren<Animator>();
        }

        ApplyAnimatorState();
    }

    private void OnValidate()//インスペクタ上でインスペクタのプロパティを変更した直後に自動で呼ばれる関数。
    {
        walkSpeed = Mathf.Max(0f, walkSpeed);
        dashSpeed = Mathf.Max(0f, dashSpeed);
        dashDuration = Mathf.Max(0f, dashDuration);
        dashRecoveryTime = Mathf.Max(0f, dashRecoveryTime);
        invincibilityDuration = Mathf.Max(0f, invincibilityDuration);
        dashCooldown = Mathf.Max(0f, dashCooldown);
        inputDeadZone = Mathf.Clamp(inputDeadZone, 0f, 1f);

        if (invincibilityDuration > dashDuration)
        {
            invincibilityDuration = dashDuration;
        }
    }

    private void OnEnable()//シーン読み込み後やコンポーネント有効化時に起動する
    {
        if (moveAction != null && moveAction.action != null)//Actionが設定されているかの確認。
        {
            moveAction.action.Enable();//設定されているActionの有効化
        }

        if (dashAction != null && dashAction.action != null)
        {
            dashAction.action.Enable();//設定されているActionの有効化
            dashAction.action.performed += OnDashPerformed;//ダッシュボタンが押された瞬間に OnDashPerformed が呼ばれるようイベントハンドラーを登録
        }
    }

    private void OnDisable()//OnEnableの逆の処理
    {
        if (dashAction != null && dashAction.action != null)
        {
            dashAction.action.performed -= OnDashPerformed;
            dashAction.action.Disable();
        }

        if (moveAction != null && moveAction.action != null)
        {
            moveAction.action.Disable();
        }
    }

    private void Update()
    {
        UpdateCooldownTimer();

        switch (currentState)
        {
            case PlayerState.Idle:
                UpdateIdleState();
                break;

            case PlayerState.Walk:
                UpdateWalkState();
                break;

            case PlayerState.Dash:
                UpdateDashState();
                break;

            case PlayerState.DashRecovery:
                UpdateDashRecoveryState();
                break;

            case PlayerState.Attack:
                UpdateAttackState();
                break;

            case PlayerState.Guard:
                UpdateGuardState();
                break;
        }
        //使っていない軸、今回はyとzのposを固定する
        LockUnusedAxes();
    }

    private void UpdateIdleState()
    {
        isInvincible = false;

        float inputX = ReadMoveInputX();
        //次の状態への遷移条件
        if (Mathf.Abs(inputX) >= inputDeadZone)//xの絶対値がDeadZoneより大きいとき
        {
            UpdateFacingDirection(inputX);
            MoveHorizontally(inputX);
            SetState(PlayerState.Walk);
        }
    }

    private void UpdateWalkState()
    {
        isInvincible = false;

        float inputX = ReadMoveInputX();
        //前の状態への遷移条件
        if (Mathf.Abs(inputX) < inputDeadZone)
        {
            SetState(PlayerState.Idle);
            return;
        }

        UpdateFacingDirection(inputX);
        MoveHorizontally(inputX);
    }

    private void UpdateDashState()
    {
        dashTimer -= Time.deltaTime;
        invincibilityTimer -= Time.deltaTime;

        Vector3 move = new Vector3(dashDirection * dashSpeed * Time.deltaTime, 0f, 0f);
        transform.position += move;
        //無敵時間ないかどうか
        if (invincibilityTimer > 0f)
        {
            isInvincible = true;
        }
        else
        {
            isInvincible = false;
        }

        if (dashTimer <= 0f)
        {
            StartDashRecovery();
        }
    }
    //DashRecoveryに入った際の処理とDashRecoveryを終えるための処理の開始
    private void UpdateDashRecoveryState()
    {
        recoveryTimer -= Time.deltaTime;

        // 硬直中は完全停止・入力無視・無敵なし
        isInvincible = false;

        if (recoveryTimer <= 0f)
        {
            EndDashRecovery();
        }
    }

    private void UpdateAttackState()
    {
        // プレースホルダー
        // 今後ここに攻撃中の処理を書く
        // 現時点では移動・ダッシュ入力は受け付けない
        isInvincible = false;
    }

    private void UpdateGuardState()
    {
        // プレースホルダー
        // 今後ここにガード中の処理を書く
        // 現時点では移動・ダッシュ入力は受け付けない
        isInvincible = false;
    }

    private void OnDashPerformed(InputAction.CallbackContext context)//ボタンが押された際にこの関数が起動、その設定は最初の方で行なっている
    {
        if (!CanDash)
        {
            return;
        }

        float inputX = ReadMoveInputX();

        if (Mathf.Abs(inputX) < inputDeadZone)
        {
            return;
        }
        //ダッシュの方向を定義
        dashDirection = inputX > 0f ? 1 : -1;
        //向きを変える
        UpdateFacingDirection(dashDirection);
        //Dash状態への遷移
        StartDash();
    }

    private void StartDash()//Dash状態への状態遷移とタイマーのセット
    {
        dashTimer = dashDuration;
        invincibilityTimer = invincibilityDuration;
        isInvincible = invincibilityTimer > 0f;

        SetState(PlayerState.Dash);
    }

    private void StartDashRecovery()//DashRecovery状態への状態遷移とタイマーのセット
    {
        recoveryTimer = dashRecoveryTime;
        isInvincible = false;

        SetState(PlayerState.DashRecovery);
    }

    private void EndDashRecovery()//DashRecoveryからIdleへの状態遷移とタイマーのセット
    {
        recoveryTimer = 0f;
        isInvincible = false;

        // 硬直終了後からクールダウン開始
        cooldownTimer = dashCooldown;

        SetState(PlayerState.Idle);
    }

    private void MoveHorizontally(float inputX)//移動関数、transform移動において非常に汎用性の高い関数
    {
        float direction = Mathf.Sign(inputX);//数値の正負だけを取り出す

        // 格闘ゲームのステップ移動では
        // 「一定速度で動く」「ピタッと止まる」制御が重要。
        // Rigidbody だと慣性や物理挙動の影響を受けやすいため、
        // この仕様では Transform の直接移動の方が扱いやすい。
        Vector3 move = new Vector3(direction * walkSpeed * Time.deltaTime, 0f, 0f);//方向とスピードと経過時間から動くべき距離を計算して動かす。transformによる位置移動なら定番の処理
        transform.position += move;
    }

    private void UpdateFacingDirection(float inputX)//向きを変える関数
    {
        if (Mathf.Abs(inputX) < inputDeadZone)
        {
            return;
        }

        bool shouldFaceRight = inputX > 0f;//右へ向かなければいけないかどうかのフラグ

        if (shouldFaceRight == isFacingRight)//右に向かう必要があるかつもう右に向いている
        {
            return;
        }

        isFacingRight = shouldFaceRight;

        Vector3 scale = transform.localScale;
        scale.x = isFacingRight ? defaultScaleX : -defaultScaleX;
        transform.localScale = scale;
    }

    private void SetState(PlayerState newState)//現在の状態の更新とAnimatorへの対応の開始
    {
        if (currentState == newState)
        {
            return;
        }

        currentState = newState;
        ApplyAnimatorState();
    }

    private void ApplyAnimatorState()//Animotor側での状態遷移に対する対応
    {
        if (animator == null)
        {
            return;
        }

        bool isWalking = currentState == PlayerState.Walk;
        animator.SetBool("IsWalking", isWalking);

        if (currentState == PlayerState.Dash)
        {
            animator.ResetTrigger("Dash");
            animator.SetTrigger("Dash");
        }
    }

    private void UpdateCooldownTimer()//クールダウンのタイマー
    {
        if (cooldownTimer > 0f)
        {
            cooldownTimer -= Time.deltaTime;

            if (cooldownTimer < 0f)
            {
                cooldownTimer = 0f;
            }
        }
    }

    private float ReadMoveInputX()//ボタンの入力を返す。
    {
        if (moveAction == null || moveAction.action == null)
        {
            return 0f;
        }

        Vector2 input = moveAction.action.ReadValue<Vector2>();//ボタンの入力を受け付ける
        return input.x;
    }

    private void LockUnusedAxes()//yとzのposを固定
    {
        Vector3 pos = transform.position;

        if (lockYPosition)
        {
            pos.y = initialY;
        }

        if (lockZPosition)
        {
            pos.z = initialZ;
        }

        transform.position = pos;
    }
}