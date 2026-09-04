-- End-to-end test project for the DeviceIdentifier pipeline.
-- Generated - do not hand-edit. Rerun gen.py to change the set.
--
-- 84 devices, chosen so every block has a predictable outcome.
-- Column names match what PythonSQL reads: ProjectCode, CustomerCode, DataIds.
-- If dbo.DummyTestingData has other NOT NULL columns, add them here.

DELETE FROM dbo.DummyTestingData WHERE ProjectCode = 'A9997';

INSERT INTO dbo.DummyTestingData (ProjectCode, CustomerCode, DataIds) VALUES
    ('A9997'  , 'OILTEK' , 'A101'        ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'AG102'       ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'AGM103'      ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'CPM104'      ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'CT105'       ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'CV106'       ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'DV107'       ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'EP108'       ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'ES109'       ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'F110'        ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'FB111'       ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'FCV112'      ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'FIT113'      ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'FITQ114'     ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'FL115'       ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'FM116'       ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'FN117'       ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'FP118'       ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'FS119'       ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'FT120'       ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'FTM121'      ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'FTPL122'     ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'HHLA123'     ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'HL124'       ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'HLA125'      ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'HLAT126'     ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'HM127'       ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'HPP128'      ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'HST129'      ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'HT130'       ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'ICM131'      ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'KB132'       ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'LAH133'      ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'LAHH134'     ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'LAL135'      ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'LCV136'      ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'LL137'       ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'LLA138'      ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'LLAT139'     ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'LPM140'      ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'LS141'       ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'LSH142'      ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'LSL143'      ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'LT144'       ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'LTT145'      ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'LTTU146'     ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'M147'        ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'MF148'       ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'ML149'       ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'MU150'       ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'NV151'       ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'OC152'       ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'P153'        ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'PC154'       ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'PF155'       ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'PFM156'      ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'PLM157'      ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'PRM158'      ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'PRV159'      ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'PS160'       ),  -- A. Known prefix
    ('A9997'  , 'OILTEK' , 'ZZQ201'      ),  -- B. Unknown prefix
    ('A9997'  , 'OILTEK' , 'QXX202'      ),  -- B. Unknown prefix
    ('A9997'  , 'OILTEK' , 'YYK203'      ),  -- B. Unknown prefix
    ('A9997'  , 'OILTEK' , 'WWJ204'      ),  -- B. Unknown prefix
    ('A9997'  , 'OILTEK' , 'VVN205'      ),  -- B. Unknown prefix
    ('A9997'  , 'OILTEK' , 'UUB206'      ),  -- B. Unknown prefix
    ('A9997'  , 'OILTEK' , 'TTG207'      ),  -- B. Unknown prefix
    ('A9997'  , 'OILTEK' , 'SSD208'      ),  -- B. Unknown prefix
    ('A9997'  , 'OILTEK' , 'HT301'       ),  -- C. Case variant
    ('A9997'  , 'OILTEK' , 'ht301'       ),  -- C. Case variant
    ('A9997'  , 'OILTEK' , 'PT301'       ),  -- C. Case variant
    ('A9997'  , 'OILTEK' , 'pt301'       ),  -- C. Case variant
    ('A9997'  , 'OILTEK' , 'DV301'       ),  -- C. Case variant
    ('A9997'  , 'OILTEK' , 'dv301'       ),  -- C. Case variant
    ('A9997'  , 'OILTEK' , 'FT301'       ),  -- C. Case variant
    ('A9997'  , 'OILTEK' , 'ft301'       ),  -- C. Case variant
    ('A9997'  , 'OILTEK' , 'A401'        ),  -- D. Ambiguous prefix A
    ('A9997'  , 'OILTEK' , 'A402'        ),  -- D. Ambiguous prefix A
    ('A9997'  , 'OILTEK' , 'A403'        ),  -- D. Ambiguous prefix A
    ('A9997'  , 'OILTEK' , 'A404'        ),  -- D. Ambiguous prefix A
    ('A9997'  , 'OILTEK' , 'V001.21'     ),  -- E. Formatting
    ('A9997'  , 'OILTEK' , '  HT501  '   ),  -- E. Formatting
    ('A9997'  , 'OILTEK' , 'PT-502'      ),  -- E. Formatting
    ('A9997'  , 'OILTEK' , 'LL_503'      );  -- E. Formatting

SELECT COUNT(*) AS Inserted FROM dbo.DummyTestingData WHERE ProjectCode = 'A9997';