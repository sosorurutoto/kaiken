using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerController : MonoBehaviour
{
    public enum PlayerState//行動の状態遷移
    {
        Idle,
        Walk,
        Dash,
        DashRecovery,
        Attack,
        Guard
    }

    public enum AttackType//攻撃の種類
    {
        None,
        Weak,
        Strong
    }

    public enum AttackPhase//攻撃における状態
    {
        None,
        Startup,
        Active,
        Recovery
    }

    [System.Serializable]
    private class ComboRouteSettings//考えられるコンボとそれの許可
    {
        [Header("Weak 1st")]
        public bool weak1ToWeak2 = true;
        public bool weak1ToStrong = true;

        [Header("Weak 2nd")]
        public bool weak2ToWeak3 = true;
        public bool weak2ToStrong = true;

        [Header("Weak 3rd")]
        public bool weak3ToStrong = false;
    }

    [Header("Move Settings")]//移動の時のパラメーター
    [SerializeField] private float walkSpeed = 3f;
    [SerializeField] private float dashSpeed = 12f;
    [SerializeField] private float dashDuration = 0.15f;
    [SerializeField] private float dashRecoveryTime = 0.2f;
    [SerializeField] private float invincibilityDuration = 0.05f;
    [SerializeField] private float dashCooldown = 0.5f;
    [SerializeField] private float inputDeadZone = 0.2f;

    [Header("Input Actions (New Input System)")]//Actionの管理
    [SerializeField] private InputActionReference moveAction;
    [SerializeField] private InputActionReference dashAction;
    [SerializeField] private InputActionReference weakAttackAction;
    [SerializeField] private InputActionReference strongAttackAction;

    [Header("Animator")]//Animationを登録するためのAnimator
    [SerializeField] private Animator animator;

    [Header("Visual")]//キャラクターのRoot
    [SerializeField] private Transform visualRoot;

    [Header("Attack Buffer")]//コンボの判定に使う時間
    [SerializeField] private float inputBufferTime = 0.15f;

    [Header("Weak Attack Frame Data")]//弱攻撃のフェーズの時間
    [SerializeField] private float weakAttackStartup = 0.08f;
    [SerializeField] private float weakAttackActive = 0.08f;
    [SerializeField] private float weakAttackRecovery = 0.12f;

    [Header("Strong Attack Frame Data")]
    [SerializeField] private float strongAttackStartup = 0.14f;
    [SerializeField] private float strongAttackActive = 0.10f;
    [SerializeField] private float strongAttackRecovery = 0.22f;

    [Header("Combo Cancel Settings")]//コンボのキャンセルが可能な時間
    [SerializeField, Range(0f, 1f)] private float weakActiveCancelStartNormalized = 0.5f;
    [SerializeField] private ComboRouteSettings comboRoutes = new ComboRouteSettings();

    [Header("Axis Lock")]
    [SerializeField] private bool lockYPosition = true;
    [SerializeField] private bool lockZPosition = true;

    public PlayerState CurrentState => currentState;//読み取り専用プロパティを作成、値の取得と内部制御を明確に分離する。読み取り専用のため内部状態を外部から直接値を変更されず安全性が高まる
    public AttackType CurrentAttackType => currentAttackType;//読み取り専用プロパティを作成
    public AttackPhase CurrentAttackPhase => currentAttackPhase;//読み取り専用プロパティを作成
    public bool IsInvincible => isInvincible;//無敵かどうかの値の読み取り専門のプロパティ
    public bool IsFacingRight => isFacingRight;//右を向いているかどうか
    //ダッシュ可能かどうかの値
    public bool CanDash =>
        cooldownTimer <= 0f &&
        currentState != PlayerState.Dash &&
        currentState != PlayerState.DashRecovery &&
        currentState != PlayerState.Attack &&
        currentState != PlayerState.Guard;

    private PlayerState currentState = PlayerState.Idle;
    private AttackType currentAttackType = AttackType.None;
    private AttackPhase currentAttackPhase = AttackPhase.None;

    private bool isInvincible = false;
    private bool isFacingRight = true;

    private float dashTimer = 0f;
    private float recoveryTimer = 0f;
    private float invincibilityTimer = 0f;
    private float cooldownTimer = 0f;

    private int dashDirection = 0;//ダッシュの方向
    //yとzの固定に使う値
    private float initialY;
    private float initialZ;
    private float defaultVisualScaleX = 1f;

    private AttackType bufferedAttackType = AttackType.None;
    private float attackBufferTimer = 0f;//先に押された攻撃入力を、あとで使うために何秒だけ保持するか

    //攻撃時のコルーチン
    private Coroutine attackCoroutine;
    private int weakComboStep = 0;

    private void Awake()
    {
        initialY = transform.position.y;
        initialZ = transform.position.z;

        if (visualRoot == null)
        {
            visualRoot = transform;
        }

        defaultVisualScaleX = Mathf.Abs(visualRoot.localScale.x);//通常状態のxのScaleの値を取得してそれを絶対値として向きの反転の基準値とする
        if (defaultVisualScaleX <= 0f)
        {
            defaultVisualScaleX = 1f;
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

        inputBufferTime = Mathf.Max(0f, inputBufferTime);

        weakAttackStartup = Mathf.Max(0f, weakAttackStartup);
        weakAttackActive = Mathf.Max(0f, weakAttackActive);
        weakAttackRecovery = Mathf.Max(0f, weakAttackRecovery);

        strongAttackStartup = Mathf.Max(0f, strongAttackStartup);
        strongAttackActive = Mathf.Max(0f, strongAttackActive);
        strongAttackRecovery = Mathf.Max(0f, strongAttackRecovery);

        weakActiveCancelStartNormalized = Mathf.Clamp01(weakActiveCancelStartNormalized);

        if (invincibilityDuration > dashDuration)
        {
            invincibilityDuration = dashDuration;
        }
    }

    private void OnEnable()//シーン読み込み後やコンポーネント有効化時に起動する。各種Actionの有効化と
    {
        if (moveAction != null && moveAction.action != null)
        {
            moveAction.action.Enable();
        }

        if (dashAction != null && dashAction.action != null)
        {
            dashAction.action.Enable();
            dashAction.action.performed += OnDashPerformed;//ダッシュボタンが押された瞬間に OnDashPerformed が呼ばれるようイベントハンドラーを登録
        }

        if (weakAttackAction != null && weakAttackAction.action != null)
        {
            weakAttackAction.action.Enable();
            weakAttackAction.action.performed += OnWeakAttackPerformed;//弱攻撃ボタンが押された瞬間に OnWeakAttackPerformed が呼ばれるようイベントハンドラーを登録
        }

        if (strongAttackAction != null && strongAttackAction.action != null)
        {
            strongAttackAction.action.Enable();
            strongAttackAction.action.performed += OnStrongAttackPerformed;//強攻撃ボタンが押された瞬間に OnStrongAttackPerformed が呼ばれるようイベントハンドラーを登録
        }
    }

    private void OnDisable()//OnEnableの逆の処理
    {
        if (dashAction != null && dashAction.action != null)
        {
            dashAction.action.performed -= OnDashPerformed;
            dashAction.action.Disable();
        }

        if (weakAttackAction != null && weakAttackAction.action != null)
        {
            weakAttackAction.action.performed -= OnWeakAttackPerformed;
            weakAttackAction.action.Disable();
        }

        if (strongAttackAction != null && strongAttackAction.action != null)
        {
            strongAttackAction.action.performed -= OnStrongAttackPerformed;
            strongAttackAction.action.Disable();
        }

        if (moveAction != null && moveAction.action != null)
        {
            moveAction.action.Disable();
        }

        if (attackCoroutine != null)
        {
            StopCoroutine(attackCoroutine);
            attackCoroutine = null;
        }
    }

    private void Update()//移動における制御の状態管理
    {
        UpdateCooldownTimer();
        UpdateAttackBufferTimer();//タイマーを更新

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

        if (TryStartBufferedAttackFromNeutral())//記録していた入力があればここでこの関数を終了する
        {
            return;
        }

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

        if (TryStartBufferedAttackFromNeutral())//記録していた入力があればここでこの関数を終了する
        {
            return;
        }

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
        dashTimer -= Time.deltaTime;//ダッシュの時間ないかのタイマーを開始
        invincibilityTimer -= Time.deltaTime;//無敵時間かどうかのタイマー開始

        Vector3 move = new Vector3(dashDirection * dashSpeed * Time.deltaTime, 0f, 0f);//transform移動
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

    private void UpdateAttackState()//Attack状態の際に起動
    {
        // 攻撃中は移動・ダッシュをロックする。
        // 実際の攻撃進行は Coroutine 側で厳密に管理する。
        isInvincible = false;//無敵モードではない。
    }

    private void UpdateGuardState()//ガード状態の際に起動
    {
        isInvincible = false;
    }

    private void OnDashPerformed(InputAction.CallbackContext context)//ボタンが押された際にこの関数が起動、その設定は最初の方で行なっている
    {
        if (!CanDash)//Dashが無理ならこの関数を終了
        {
            return;
        }

        float inputX = ReadMoveInputX();

        if (Mathf.Abs(inputX) < inputDeadZone)
        {
            return;
        }

        dashDirection = inputX > 0f ? 1 : -1;

        UpdateFacingDirection(dashDirection);
        StartDash();
    }

    private void OnWeakAttackPerformed(InputAction.CallbackContext context)//弱攻撃のボタンを押された際に起動する関数、ここで入力されたものを記録しておく
    {
        BufferAttackInput(AttackType.Weak);
    }

    private void OnStrongAttackPerformed(InputAction.CallbackContext context)//強攻撃のボタンを押された際に起動する関数、ここで入力されたものを記録しておく
    {
        BufferAttackInput(AttackType.Strong);
    }

    private void BufferAttackInput(AttackType attackType)//記録しておいたボタンに対応した攻撃のタイプとコンボの際に次の入力を受け付けるタイマーの時間を設定
    {
        bufferedAttackType = attackType;
        attackBufferTimer = inputBufferTime;
    }

    private bool TryStartBufferedAttackFromNeutral()//「ニュートラル状態」に戻った瞬間に、先に押されていた攻撃入力を拾って攻撃を始める関数。移動中や別の動作中に押された攻撃入力を記録しておき次に始めれるようにしておく部分は別途であり、この関数ないでは記録していたものを実際に処理する
    {
        if (currentState != PlayerState.Idle && currentState != PlayerState.Walk)//Idle または Walk 中でなければ記録しておく必要がないので何もしない
        {
            return false;
        }

        if (!HasBufferedAttack())//記録していた入力がない場合
        {
            return false;
        }

        AttackType attackToStart = bufferedAttackType;//攻撃開始を待っている状態
        ConsumeBufferedAttack();//ブッファしていた入力をなくす

        float inputX = ReadMoveInputX();//移動の入力を受け付ける
        if (Mathf.Abs(inputX) >= inputDeadZone)
        {
            UpdateFacingDirection(inputX);//向きを変える
        }

        StartAttackSequence(attackToStart);//記録していた入力の攻撃を実際に起動
        return true;
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

        // 硬直が終わった瞬間からクールダウン開始
        cooldownTimer = dashCooldown;

        SetState(PlayerState.Idle);
    }

    private void StartAttackSequence(AttackType firstAttackType)//記録していた入力の攻撃の開始
    {
        if (attackCoroutine != null)//すでにコルーチンがあった場合
        {
            StopCoroutine(attackCoroutine);//コル-チンを止める
        }

        weakComboStep = 0;
        attackCoroutine = StartCoroutine(AttackSequenceRoutine(firstAttackType));
    }

    private IEnumerator AttackSequenceRoutine(AttackType firstAttackType)
    {
        AttackType nextAttackType = firstAttackType;

        SetState(PlayerState.Attack);

        while (true)
        {
            BeginAttackStep(nextAttackType);

            // -----------------------------
            // Startup
            // -----------------------------
            currentAttackPhase = AttackPhase.Startup;//Attackの状態をStartUpに進める
            yield return WaitSimple(GetStartupTime(currentAttackType));//攻撃の種類に対応した攻撃のモーションの時間を返されその時間分処理がとまる

            // -----------------------------
            // Active
            // 後半からキャンセル可能
            // -----------------------------
            currentAttackPhase = AttackPhase.Active;//Attackの状態をActiveに進める

            float activeTime = GetActiveTime(currentAttackType);
            float cancelStartTime = GetActiveCancelStartTime(currentAttackType);

            bool canceled = false;//キャンセルされているかどうかのフラグ
            AttackType cancelAttackType = AttackType.None;//キャンセルされた攻撃のタイプの記録

            float timer = 0f;
            while (timer < activeTime)//タイマーのかいし。モーションの時間ないのみ動く
            {
                timer += Time.deltaTime;
                //キャンセル可能な攻撃タイプかつキャンセル可能な時間になっているのならキャンセル可能
                bool canCheckCancel =
                    CanCurrentAttackBeCanceled() &&
                    timer >= cancelStartTime;

                if (canCheckCancel && TryConsumeBufferedComboAttack(out cancelAttackType))//コンボ可能な入力が記録されていて尚且つキャンセルできる
                {
                    canceled = true;//キャンセルを行い次の入力に繋げる
                    break;
                }

                yield return null;
            }

            if (canceled)
            {
                nextAttackType = cancelAttackType;
                continue;
            }

            // -----------------------------
            // Recovery
            // 全期間キャンセル可能
            // ただし Strong は一切キャンセル不可
            // -----------------------------
            currentAttackPhase = AttackPhase.Recovery;

            float recoveryTime = GetRecoveryTime(currentAttackType);
            timer = 0f;//タイマーを０に戻す
            canceled = false;
            cancelAttackType = AttackType.None;

            while (timer < recoveryTime)
            {
                timer += Time.deltaTime;

                bool canCheckCancel = CanCurrentAttackBeCanceled();

                if (canCheckCancel && TryConsumeBufferedComboAttack(out cancelAttackType))//コンボ可能な入力が記録されていて尚且つキャンセルできる
                {
                    canceled = true;//キャンセルを行い次の入力に繋げる
                    break;
                }

                yield return null;
            }

            if (canceled)
            {
                nextAttackType = cancelAttackType;
                continue;
            }

            break;
        }

        FinishAttackSequence();//コルーチンを止め、様々なパラメータを初期化
    }

    private void BeginAttackStep(AttackType attackType)//アタックを開始してアタックの状態遷移を始める関数
    {
        currentAttackType = attackType;
        currentAttackPhase = AttackPhase.Startup;
        isInvincible = false;

        RegisterComboStep(attackType);//コンボの段階を進める
        SetState(PlayerState.Attack);
        PlayAttackAnimation(attackType);
    }

    private void FinishAttackSequence()//コルーチンを止める
    {
        attackCoroutine = null;
        currentAttackType = AttackType.None;
        currentAttackPhase = AttackPhase.None;
        weakComboStep = 0;
        isInvincible = false;

        SetState(PlayerState.Idle);
    }

    private void RegisterComboStep(AttackType attackType)//コンボの段階を進める
    {
        if (attackType == AttackType.Weak)
        {
            weakComboStep += 1;//弱コンボの段階を進める
        }
    }

    private bool CanCurrentAttackBeCanceled()//キャンセル可能な攻撃かを返す
    {
        // Strong は一切キャンセル不可
        if (currentAttackType == AttackType.Strong)
        {
            return false;
        }

        // Weak のみキャンセル可
        return currentAttackType == AttackType.Weak;
    }

    private bool TryConsumeBufferedComboAttack(out AttackType nextAttackType)//コンボとして使える攻撃入力がバッファされているかを判定し、使えるならその入力を消費して次の攻撃として返す関数。outを使うことで実質的に二つ戻り値を返す
    {
        nextAttackType = AttackType.None;

        if (!HasBufferedAttack())
        {
            return false;
        }

        AttackType candidate = bufferedAttackType;

        if (!IsComboRouteAllowed(candidate))//コンボ可能な入力が記録されているかどうか？
        {
            return false;
        }

        ConsumeBufferedAttack();
        nextAttackType = candidate;
        return true;
    }

    private bool IsComboRouteAllowed(AttackType nextAttackType)//コンボ可能な入力が記録されているか、また許可されているコンボなのか、ここをいじれば許可するコンボの種類が増える
    {
        if (currentAttackType == AttackType.Strong)//強攻撃
        {
            return false;
        }

        if (currentAttackType != AttackType.Weak)//弱攻撃
        {
            return false;
        }

        switch (weakComboStep)
        {
            case 1:
                if (nextAttackType == AttackType.Weak)
                {
                    return comboRoutes.weak1ToWeak2;
                }

                if (nextAttackType == AttackType.Strong)
                {
                    return comboRoutes.weak1ToStrong;
                }

                return false;

            case 2:
                if (nextAttackType == AttackType.Weak)
                {
                    return comboRoutes.weak2ToWeak3;
                }

                if (nextAttackType == AttackType.Strong)
                {
                    return comboRoutes.weak2ToStrong;
                }

                return false;

            case 3:
                if (nextAttackType == AttackType.Strong)
                {
                    return comboRoutes.weak3ToStrong;
                }

                return false;

            default:
                return false;
        }
    }

    private float GetStartupTime(AttackType attackType)//攻撃の種類に対応した攻撃のモーションの開始までの時間を返す
    {
        if (attackType == AttackType.Weak)
        {
            return weakAttackStartup;
        }

        if (attackType == AttackType.Strong)
        {
            return strongAttackStartup;
        }

        return 0f;
    }

    private float GetActiveTime(AttackType attackType)//攻撃の種類に対応した攻撃のモーションの時間を返す関数
    {
        if (attackType == AttackType.Weak)
        {
            return weakAttackActive;
        }

        if (attackType == AttackType.Strong)
        {
            return strongAttackActive;
        }

        return 0f;
    }

    private float GetRecoveryTime(AttackType attackType)//攻撃の種類に対応した攻撃のモーション後の硬直の時間を返す関数
    {
        if (attackType == AttackType.Weak)
        {
            return weakAttackRecovery;
        }

        if (attackType == AttackType.Strong)
        {
            return strongAttackRecovery;
        }

        return 0f;
    }

    private float GetActiveCancelStartTime(AttackType attackType)//入力のキャンセルを受け付ける時間を返す関数
    {
        if (attackType == AttackType.Weak)
        {
            return weakAttackActive * weakActiveCancelStartNormalized;
        }

        // Strong はキャンセル不可だが、計算用に末尾を返しておく
        return GetActiveTime(attackType);
    }

    private IEnumerator WaitSimple(float duration)
    {
        float timer = 0f;

        while (timer < duration)
        {
            timer += Time.deltaTime;
            yield return null;
        }
    }

    private bool HasBufferedAttack()//記録した入力があるかどうかを返す
    {
        return bufferedAttackType != AttackType.None && attackBufferTimer > 0f;//先に押された攻撃入力を、あとで使うために何秒だけ保持するかを表すタイマーが存在しており、Attack Typeが何もないという状態ではないときtrueを返す
    }

    private void ConsumeBufferedAttack()//記録していた入力を消す
    {
        bufferedAttackType = AttackType.None;
        attackBufferTimer = 0f;
    }

    private void MoveHorizontally(float inputX)//移動関数、transform移動において非常に汎用性の高い関数
    {
        float direction = Mathf.Sign(inputX);//数値の正負だけを取り出す

        // 格闘ゲームのステップ移動では
        // 「一定速度で動く」「ピタッと止まる」制御が重要。
        // Rigidbody だと慣性や物理挙動の影響を受けやすいため、
        // この仕様では Transform の直接移動の方がフレーム単位で扱いやすい。
        Vector3 move = new Vector3(direction * walkSpeed * Time.deltaTime, 0f, 0f);//方向とスピードと経過時間から動くべき距離を計算して動かす。transformによる位置移動なら定番の処理
        transform.position += move;
    }

    private void UpdateFacingDirection(float inputX)//向きを変える関数
    {
        if (visualRoot == null)
        {
            return;
        }

        if (Mathf.Abs(inputX) < inputDeadZone)
        {
            return;
        }

        bool shouldFaceRight = inputX > 0f;//右へ向かなければいけないかどうかのフラグ

        //右に向かう必要があるかつもう右に向いている
        if (shouldFaceRight == isFacingRight)
        {
            return;
        }

        isFacingRight = shouldFaceRight;

        Vector3 scale = visualRoot.localScale;
        scale.x = isFacingRight ? defaultVisualScaleX : -defaultVisualScaleX;
        visualRoot.localScale = scale;
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

        animator.SetBool("IsWalking", currentState == PlayerState.Walk);

        if (currentState == PlayerState.Dash)
        {
            animator.ResetTrigger("Dash");
            animator.SetTrigger("Dash");
        }
    }

    private void PlayAttackAnimation(AttackType attackType)//アタックの際のAnimationを実際に動作させる部分
    {
        if (animator == null)
        {
            return;
        }

        animator.SetBool("IsWalking", false);

        animator.ResetTrigger("WeakAttack");
        animator.ResetTrigger("StrongAttack");

        if (attackType == AttackType.Weak)
        {
            animator.SetTrigger("WeakAttack");
        }
        else if (attackType == AttackType.Strong)
        {
            animator.SetTrigger("StrongAttack");
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

    private void UpdateAttackBufferTimer()//入力を記録しておくブッファのタイマー
    {
        if (attackBufferTimer > 0f)
        {
            attackBufferTimer -= Time.deltaTime;

            if (attackBufferTimer <= 0f)
            {
                ConsumeBufferedAttack();
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