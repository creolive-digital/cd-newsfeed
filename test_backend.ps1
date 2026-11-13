$ErrorActionPreference = "Stop"
$baseUrl = "http://localhost:5055"

function Print-Result($name, $passed, $details = "") {
    if ($passed) {
        Write-Host " [PASS] $name" -ForegroundColor Green
    } else {
        Write-Host " [FAIL] $name - $details" -ForegroundColor Red
        throw "Test failed: $name - $details"
    }
}

Write-Host "`n=== CD-NEWSFEED BACKEND INTEGRATION TESTS ===`n" -ForegroundColor Cyan

# 1. Unauthenticated endpoints
try {
    Invoke-RestMethod -Uri "$baseUrl/api/auth/me" -Method Get -ErrorAction Stop
    Print-Result "Unauthenticated /api/auth/me returns 401" $false "Expected 401 but succeeded"
} catch {
    $code = $_.Exception.Response.StatusCode.value__
    Print-Result "Unauthenticated /api/auth/me returns 401" ($code -eq 401) "Status: $code"
}

try {
    Invoke-RestMethod -Uri "$baseUrl/api/feed" -Method Get -ErrorAction Stop
    Print-Result "Unauthenticated /api/feed returns 401" $false "Expected 401 but succeeded"
} catch {
    $code = $_.Exception.Response.StatusCode.value__
    Print-Result "Unauthenticated /api/feed returns 401" ($code -eq 401) "Status: $code"
}

# 2. Login or Register Admin
$adminSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$adminLoginBody = @{
    username = "admin"
    password = "AdminPassword123!"
} | ConvertTo-Json

try {
    $loginResp = Invoke-RestMethod -Uri "$baseUrl/api/auth/login" -Method Post -Body $adminLoginBody -ContentType "application/json" -WebSession $adminSession -ErrorAction Stop
    Print-Result "Admin login succeeds" ($loginResp.role -eq "Admin" -and $loginResp.username -eq "admin")
} catch {
    # If not registered yet, register
    $adminRegBody = @{
        displayName = "Admin User"
        username = "admin"
        password = "AdminPassword123!"
    } | ConvertTo-Json
    $loginResp = Invoke-RestMethod -Uri "$baseUrl/api/auth/register" -Method Post -Body $adminRegBody -ContentType "application/json" -WebSession $adminSession
    Print-Result "Register 1st user assigns Admin role" ($loginResp.role -eq "Admin")
}

# 3. GET /api/auth/me as Admin
$meResp = Invoke-RestMethod -Uri "$baseUrl/api/auth/me" -Method Get -WebSession $adminSession
Print-Result "Admin /api/auth/me succeeds" ($meResp.username -eq "admin" -and $meResp.role -eq "Admin")

# 4. Load whatever approved posts currently exist in the database; an empty feed is valid.
$feed = @(Invoke-RestMethod -Uri "$baseUrl/api/feed" -Method Get -WebSession $adminSession)
$unapprovedFeedItems = @($feed | Where-Object { $_.status -ne "Approved" })
Print-Result "Feed contains only approved database posts" ($unapprovedFeedItems.Count -eq 0) "Unapproved count: $($unapprovedFeedItems.Count)"

# 5. Login or Register a Test Regular User
$suffix = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$testUsername = "user_$suffix"
$userSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$userRegBody = @{
    displayName = "Kasun Perera $suffix"
    username = $testUsername
    password = "KasunPassword123!"
} | ConvertTo-Json

$userRegResp = Invoke-RestMethod -Uri "$baseUrl/api/auth/register" -Method Post -Body $userRegBody -ContentType "application/json" -WebSession $userSession
Print-Result "Register new user assigns User role" ($userRegResp.role -eq "User" -and $userRegResp.username -eq $testUsername)
$testUserId = $userRegResp.id

# 6. Duplicate username registration check
try {
    $dupBody = @{
        displayName = "Duplicate User"
        username = $testUsername.ToUpper() # Case insensitive
        password = "SomePassword123!"
    } | ConvertTo-Json
    Invoke-RestMethod -Uri "$baseUrl/api/auth/register" -Method Post -Body $dupBody -ContentType "application/json" -ErrorAction Stop
    Print-Result "Duplicate username blocked (409)" $false "Expected 409 Conflict"
} catch {
    $code = $_.Exception.Response.StatusCode.value__
    Print-Result "Duplicate username blocked (409)" ($code -eq 409) "Status: $code"
}

# 7. Regular user tries admin endpoint (should be 403 Forbidden)
try {
    Invoke-RestMethod -Uri "$baseUrl/api/admin/users" -Method Get -WebSession $userSession -ErrorAction Stop
    Print-Result "Regular user blocked from /api/admin/users (403)" $false "Expected 403 Forbidden"
} catch {
    $code = $_.Exception.Response.StatusCode.value__
    Print-Result "Regular user blocked from /api/admin/users (403)" ($code -eq 403) "Status: $code"
}

# 8. Regular user submits a new post
$postBody = @{
    sinhalaTitle = "ශ්‍රී ලංකාවේ නවතම AI පර්යේෂණ ව්‍යාපෘතියක් $suffix"
    sinhalaSummary = "මෙරට විශ්වවිද්‍යාල සිසුන් පිරිසක් එක්ව ස්වභාවික භාෂා සැකසුම් (NLP) සඳහා නව සිංහල AI ආකෘතියක් නිර්මාණය කර ඇත."
    tag = "#Community"
    link = "https://example.com/sl-ai-project"
} | ConvertTo-Json

$createPostResp = Invoke-RestMethod -Uri "$baseUrl/api/posts" -Method Post -Body $postBody -ContentType "application/json" -WebSession $userSession
Print-Result "User submits post with Status=Pending" ($createPostResp.status -eq "Pending" -and $createPostResp.author.username -eq $testUsername)
$createdPostId = $createPostResp.id

# 9. Verify pending post does not appear in public feed yet
$feedAfterUserPost = Invoke-RestMethod -Uri "$baseUrl/api/feed" -Method Get -WebSession $userSession
$inFeed = $feedAfterUserPost | Where-Object { $_.id -eq $createdPostId }
Print-Result "Pending post not in /api/feed" ($null -eq $inFeed)

# 10. Admin checks pending posts
$pending = Invoke-RestMethod -Uri "$baseUrl/api/admin/posts/pending" -Method Get -WebSession $adminSession
$foundPending = $pending | Where-Object { $_.id -eq $createdPostId }
Print-Result "Admin sees pending post" ($null -ne $foundPending)

# 11. Admin approves post
$approveResp = Invoke-RestMethod -Uri "$baseUrl/api/admin/posts/$createdPostId/approve" -Method Patch -WebSession $adminSession
Print-Result "Admin approves post (Status=Approved)" ($approveResp.status -eq "Approved")

# 12. Approved post now in feed
$feedAfterApprove = Invoke-RestMethod -Uri "$baseUrl/api/feed" -Method Get -WebSession $userSession
$inFeedNow = $feedAfterApprove | Where-Object { $_.id -eq $createdPostId }
Print-Result "Approved post now appears in /api/feed" ($null -ne $inFeedNow)

# 13. Add comments to approved post
$commentBody1 = @{ content = "නියම ව්‍යාපෘතියක්! සුබ පැතුම්!" } | ConvertTo-Json
$commentResp1 = Invoke-RestMethod -Uri "$baseUrl/api/posts/$createdPostId/comments" -Method Post -Body $commentBody1 -ContentType "application/json" -WebSession $adminSession
Print-Result "Admin comments on post" ($commentResp1.user.username -eq "admin" -and $commentResp1.postId -eq $createdPostId)
$commentId1 = $commentResp1.id

$commentBody2 = @{ content = "ස්තූතියි සහයෝගයට!" } | ConvertTo-Json
$commentResp2 = Invoke-RestMethod -Uri "$baseUrl/api/posts/$createdPostId/comments" -Method Post -Body $commentBody2 -ContentType "application/json" -WebSession $userSession
Print-Result "User comments on post" ($commentResp2.user.username -eq $testUsername -and $commentResp2.postId -eq $createdPostId)

# 14. Feed includes comments with commenter username & display name
$feedWithComments = Invoke-RestMethod -Uri "$baseUrl/api/feed" -Method Get -WebSession $userSession
$targetPost = $feedWithComments | Where-Object { $_.id -eq $createdPostId }
Print-Result "Post in feed contains both comments" ($targetPost.comments.Count -ge 2)

# 15. Admin deletes a comment
$delCommentResp = Invoke-RestMethod -Uri "$baseUrl/api/admin/comments/$commentId1" -Method Delete -WebSession $adminSession
Print-Result "Admin deletes comment" ($null -ne $delCommentResp.message)

# 16. Admin gets all users
$allUsers = Invoke-RestMethod -Uri "$baseUrl/api/admin/users" -Method Get -WebSession $adminSession
Print-Result "Admin lists all users" ($allUsers.Count -ge 2) "Count: $($allUsers.Count)"

# 17. Admin deactivates user
$toggleResp = Invoke-RestMethod -Uri "$baseUrl/api/admin/users/$testUserId/toggle-active" -Method Patch -WebSession $adminSession
Print-Result "Admin toggles user IsActive to false" ($toggleResp.isActive -eq $false)

# 18. Deactivated user blocked from /api/feed
try {
    Invoke-RestMethod -Uri "$baseUrl/api/feed" -Method Get -WebSession $userSession -ErrorAction Stop
    Print-Result "Deactivated user blocked from feed (403)" $false "Expected 403"
} catch {
    $code = $_.Exception.Response.StatusCode.value__
    Print-Result "Deactivated user blocked from feed (403)" ($code -eq 403) "Status: $code"
}

# 19. Deactivated user blocked on login with clear message
try {
    $loginDeactivated = @{ username = $testUsername; password = "KasunPassword123!" } | ConvertTo-Json
    Invoke-RestMethod -Uri "$baseUrl/api/auth/login" -Method Post -Body $loginDeactivated -ContentType "application/json" -ErrorAction Stop
    Print-Result "Deactivated user login blocked (403)" $false "Expected 403"
} catch {
    $code = $_.Exception.Response.StatusCode.value__
    $stream = $_.Exception.Response.GetResponseStream()
    $reader = New-Object System.IO.StreamReader($stream)
    $body = $reader.ReadToEnd()
    Print-Result "Deactivated user login blocked with message" ($code -eq 403 -and $body -like "*deactivated*") "Body: $body"
}

# 20. Admin reactivates user
$toggleResp2 = Invoke-RestMethod -Uri "$baseUrl/api/admin/users/$testUserId/toggle-active" -Method Patch -WebSession $adminSession
Print-Result "Admin reactivates user (IsActive=true)" ($toggleResp2.isActive -eq $true)

# 21. Reactivated user logs in successfully
$newLoginSession = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$loginActive = @{ username = $testUsername; password = "KasunPassword123!" } | ConvertTo-Json
$loginActiveResp = Invoke-RestMethod -Uri "$baseUrl/api/auth/login" -Method Post -Body $loginActive -ContentType "application/json" -WebSession $newLoginSession
Print-Result "Reactivated user login succeeds" ($loginActiveResp.username -eq $testUsername)

# 22. User logout
$logoutResp = Invoke-RestMethod -Uri "$baseUrl/api/auth/logout" -Method Post -WebSession $newLoginSession
Print-Result "User logout succeeds" ($null -ne $logoutResp.message)

try {
    Invoke-RestMethod -Uri "$baseUrl/api/auth/me" -Method Get -WebSession $newLoginSession -ErrorAction Stop
    Print-Result "Post-logout /api/auth/me returns 401" $false "Expected 401"
} catch {
    $code = $_.Exception.Response.StatusCode.value__
    Print-Result "Post-logout /api/auth/me returns 401" ($code -eq 401)
}

# 23. Admin deletes user post
$delPostResp = Invoke-RestMethod -Uri "$baseUrl/api/admin/posts/$createdPostId" -Method Delete -WebSession $adminSession
Print-Result "Admin deletes post" ($null -ne $delPostResp.message)

Write-Host "`n>>> ALL 23 INTEGRATION TESTS PASSED! <<<`n" -ForegroundColor Green
